#include <algorithm>
#include <array>
#include <cerrno>
#include <cstdint>
#include <cstring>
#include <new>

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
}

namespace
{
    constexpr int AudioFormatAlac = 0x00040000;
    constexpr int AudioFormatAac = 0x00400000;
    constexpr int AudioFormatAacEld = 0x01000000;
    constexpr int MaximumInputLength = 64 * 1024;
    constexpr int MaximumOutputLength = 64 * 1024;
    constexpr int OutputChannels = 2;
    constexpr int OutputBytesPerSample = 2;

    struct Decoder
    {
        AVCodecContext* codecContext = nullptr;
        AVPacket* packet = nullptr;
        AVFrame* frame = nullptr;
        SwrContext* resampler = nullptr;
        AVChannelLayout inputLayout{};
        AVSampleFormat inputFormat = AV_SAMPLE_FMT_NONE;
        int inputSampleRate = 0;
        bool hasInputLayout = false;

        ~Decoder()
        {
            swr_free(&resampler);
            if (hasInputLayout)
                av_channel_layout_uninit(&inputLayout);
            av_frame_free(&frame);
            av_packet_free(&packet);
            avcodec_free_context(&codecContext);
        }
    };

    void WriteBigEndian16(std::uint8_t* destination, std::uint16_t value)
    {
        destination[0] = static_cast<std::uint8_t>(value >> 8);
        destination[1] = static_cast<std::uint8_t>(value);
    }

    void WriteBigEndian32(std::uint8_t* destination, std::uint32_t value)
    {
        destination[0] = static_cast<std::uint8_t>(value >> 24);
        destination[1] = static_cast<std::uint8_t>(value >> 16);
        destination[2] = static_cast<std::uint8_t>(value >> 8);
        destination[3] = static_cast<std::uint8_t>(value);
    }

    bool SetExtraData(AVCodecContext* context, int audioFormat, int sampleRate, int channels, int bitDepth, int frameLength)
    {
        std::uint8_t data[36]{};
        int dataLength = 0;

        if (audioFormat == AudioFormatAlac)
        {
            // ALACSpecificConfig wrapped in an 'alac' atom, as required by the
            // raw ALAC decoder. AirPlay negotiates 352 frames at 44.1 kHz.
            WriteBigEndian32(data, static_cast<std::uint32_t>(sizeof(data)));
            data[4] = 'a';
            data[5] = 'l';
            data[6] = 'a';
            data[7] = 'c';
            WriteBigEndian32(data + 12, static_cast<std::uint32_t>(frameLength));
            data[16] = 0;
            data[17] = static_cast<std::uint8_t>(bitDepth);
            data[18] = 40;
            data[19] = 10;
            data[20] = 14;
            data[21] = static_cast<std::uint8_t>(channels);
            WriteBigEndian16(data + 22, 255);
            WriteBigEndian32(data + 24, 0);
            WriteBigEndian32(data + 28, 0);
            WriteBigEndian32(data + 32, static_cast<std::uint32_t>(sampleRate));
            dataLength = static_cast<int>(sizeof(data));
        }
        else if (audioFormat == AudioFormatAacEld)
        {
            // MPEG-4 AudioSpecificConfig: AAC-ELD, 44.1 kHz, stereo, 480 SPF.
            constexpr std::uint8_t config[] = { 0xF8, 0xE8, 0x50, 0x00 };
            std::memcpy(data, config, sizeof(config));
            dataLength = static_cast<int>(sizeof(config));
        }
        else if (audioFormat == AudioFormatAac)
        {
            // MPEG-4 AudioSpecificConfig: AAC-LC, 44.1 kHz, stereo.
            constexpr std::uint8_t config[] = { 0x12, 0x10 };
            std::memcpy(data, config, sizeof(config));
            dataLength = static_cast<int>(sizeof(config));
        }
        else
        {
            return false;
        }

        context->extradata = static_cast<std::uint8_t*>(
            av_mallocz(static_cast<std::size_t>(dataLength) + AV_INPUT_BUFFER_PADDING_SIZE));
        if (context->extradata == nullptr)
            return false;

        std::memcpy(context->extradata, data, dataLength);
        context->extradata_size = dataLength;
        return true;
    }

    int ConfigureResampler(Decoder* decoder)
    {
        int sampleRate = decoder->frame->sample_rate > 0
            ? decoder->frame->sample_rate
            : decoder->codecContext->sample_rate;
        if (sampleRate <= 0 || decoder->frame->ch_layout.nb_channels <= 0)
            return AVERROR_INVALIDDATA;

        AVSampleFormat sampleFormat = static_cast<AVSampleFormat>(decoder->frame->format);
        bool configurationMatches = decoder->resampler != nullptr &&
            decoder->inputFormat == sampleFormat &&
            decoder->inputSampleRate == sampleRate &&
            decoder->hasInputLayout &&
            av_channel_layout_compare(&decoder->inputLayout, &decoder->frame->ch_layout) == 0;
        if (configurationMatches)
            return 0;

        swr_free(&decoder->resampler);
        if (decoder->hasInputLayout)
        {
            av_channel_layout_uninit(&decoder->inputLayout);
            decoder->hasInputLayout = false;
        }

        int result = av_channel_layout_copy(&decoder->inputLayout, &decoder->frame->ch_layout);
        if (result < 0)
            return result;
        decoder->hasInputLayout = true;

        AVChannelLayout outputLayout = AV_CHANNEL_LAYOUT_STEREO;
        result = swr_alloc_set_opts2(
            &decoder->resampler,
            &outputLayout,
            AV_SAMPLE_FMT_S16,
            44'100,
            &decoder->inputLayout,
            sampleFormat,
            sampleRate,
            0,
            nullptr);
        av_channel_layout_uninit(&outputLayout);
        if (result < 0)
            return result;

        result = swr_init(decoder->resampler);
        if (result < 0)
        {
            swr_free(&decoder->resampler);
            return result;
        }

        decoder->inputFormat = sampleFormat;
        decoder->inputSampleRate = sampleRate;
        return 0;
    }
}

#define WINDPLAY_EXPORT extern "C" __declspec(dllexport)

WINDPLAY_EXPORT void* __cdecl windplay_audio_decoder_create(
    int audioFormat,
    int sampleRate,
    int channels,
    int bitDepth,
    int frameLength)
{
    // WindPlay accepts only the three profiles it advertises. Keeping this
    // boundary narrow avoids attacker-controlled allocations and resampling.
    if (sampleRate != 44'100 || channels != 2 || bitDepth != 16)
        return nullptr;

    const bool isExpectedProfile =
        (audioFormat == AudioFormatAlac && frameLength == 352) ||
        (audioFormat == AudioFormatAacEld && frameLength == 480) ||
        (audioFormat == AudioFormatAac && frameLength == 1'024);
    if (!isExpectedProfile)
        return nullptr;

    AVCodecID codecId = AV_CODEC_ID_NONE;
    if (audioFormat == AudioFormatAlac)
        codecId = AV_CODEC_ID_ALAC;
    else if (audioFormat == AudioFormatAac || audioFormat == AudioFormatAacEld)
        codecId = AV_CODEC_ID_AAC;
    else
        return nullptr;

    const AVCodec* codec = avcodec_find_decoder(codecId);
    if (codec == nullptr)
        return nullptr;

    Decoder* decoder = new (std::nothrow) Decoder();
    if (decoder == nullptr)
        return nullptr;

    decoder->codecContext = avcodec_alloc_context3(codec);
    decoder->packet = av_packet_alloc();
    decoder->frame = av_frame_alloc();
    if (decoder->codecContext == nullptr || decoder->packet == nullptr || decoder->frame == nullptr)
    {
        delete decoder;
        return nullptr;
    }

    decoder->codecContext->sample_rate = sampleRate;
    decoder->codecContext->bits_per_raw_sample = bitDepth;
    decoder->codecContext->frame_size = frameLength;
    decoder->codecContext->thread_count = 1;
    decoder->codecContext->flags |= AV_CODEC_FLAG_LOW_DELAY;
    av_channel_layout_default(&decoder->codecContext->ch_layout, channels);

    if (!SetExtraData(decoder->codecContext, audioFormat, sampleRate, channels, bitDepth, frameLength) ||
        avcodec_open2(decoder->codecContext, codec, nullptr) < 0)
    {
        delete decoder;
        return nullptr;
    }

    return decoder;
}

WINDPLAY_EXPORT int __cdecl windplay_audio_decoder_decode(
    void* decoderHandle,
    const std::uint8_t* input,
    int inputLength,
    std::uint8_t* output,
    int outputCapacity)
{
    if (decoderHandle == nullptr || input == nullptr || output == nullptr ||
        inputLength <= 0 || inputLength > MaximumInputLength ||
        outputCapacity <= 0 || outputCapacity > MaximumOutputLength)
        return AVERROR(EINVAL);

    Decoder* decoder = static_cast<Decoder*>(decoderHandle);
    av_packet_unref(decoder->packet);
    int result = av_new_packet(decoder->packet, inputLength);
    if (result < 0)
        return result;
    std::memcpy(decoder->packet->data, input, inputLength);

    result = avcodec_send_packet(decoder->codecContext, decoder->packet);
    if (result < 0)
        return result;

    int totalBytes = 0;
    while (true)
    {
        result = avcodec_receive_frame(decoder->codecContext, decoder->frame);
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF)
            break;
        if (result < 0)
            return result;

        result = ConfigureResampler(decoder);
        if (result < 0)
        {
            av_frame_unref(decoder->frame);
            return result;
        }

        int availableFrames = (outputCapacity - totalBytes) / (OutputChannels * OutputBytesPerSample);
        int requiredFrames = swr_get_out_samples(decoder->resampler, decoder->frame->nb_samples);
        if (availableFrames <= 0 || requiredFrames < 0 || requiredFrames > availableFrames)
        {
            av_frame_unref(decoder->frame);
            return AVERROR(ENOSPC);
        }

        std::uint8_t* outputPlanes[] = { output + totalBytes };
        std::array<const std::uint8_t*, 8> inputPlanes{};
        int inputPlaneCount = av_sample_fmt_is_planar(
            static_cast<AVSampleFormat>(decoder->frame->format))
            ? decoder->frame->ch_layout.nb_channels
            : 1;
        if (inputPlaneCount <= 0 || inputPlaneCount > static_cast<int>(inputPlanes.size()))
        {
            av_frame_unref(decoder->frame);
            return AVERROR_INVALIDDATA;
        }
        std::copy_n(decoder->frame->extended_data, inputPlaneCount, inputPlanes.begin());

        int convertedFrames = swr_convert(
            decoder->resampler,
            outputPlanes,
            availableFrames,
            inputPlanes.data(),
            decoder->frame->nb_samples);
        if (convertedFrames < 0)
        {
            av_frame_unref(decoder->frame);
            return convertedFrames;
        }

        totalBytes += convertedFrames * OutputChannels * OutputBytesPerSample;
        av_frame_unref(decoder->frame);
    }

    return totalBytes;
}

WINDPLAY_EXPORT void __cdecl windplay_audio_decoder_destroy(void* decoderHandle)
{
    delete static_cast<Decoder*>(decoderHandle);
}
