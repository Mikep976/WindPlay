using AirPlay.Core2.Models;
using AirPlay.Core2.Models.Messages;
using AirPlay.Core2.Models.Messages.Audio;
using AirPlay.Core2.Models.Messages.Rtsp;
using AirPlay.Core2.Utils;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Rebex.Security.Cryptography;
using System.Globalization;
using System.Text;
using static AirPlay.Core2.Models.Messages.Rtsp.RtspResponseMessage;

namespace AirPlay.Core2.Connections;

public partial class RtspConnection
{
    private const int MaximumSetupBodyBytes = 256 * 1024;
    private const int MaximumInfoBodyBytes = 64 * 1024;
    private const int MaximumTextParameterBytes = 4 * 1024;
    private const int MaximumMetadataBytes = 512 * 1024;
    private const string SupportedMethods = "SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER";

    private async Task OnGetInfoRequested(
        RtspRequestMessage requestMessage,
        RtspResponseMessage responseMessage,
        CancellationToken cancellationToken)
    {
        if (requestMessage.Headers.ContainsKey("Content-Type"))
        {
            string? mediaType = null;
            if (requestMessage.Headers.TryGetSingleValue("Content-Type", out string? contentType))
            {
                int separator = contentType.IndexOf(';');
                mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();
            }

            if (!string.Equals(
                    mediaType,
                    "application/x-apple-binary-plist",
                    StringComparison.OrdinalIgnoreCase) ||
                requestMessage.Body.Length > MaximumInfoBodyBytes)
                throw new InvalidDataException("The sender provided an invalid AirPlay info qualifier request.");

            Dictionary<string, object> qualifiedInfo = [];
            if (requestMessage.Body.Length > 0)
            {
                if (PropertyListParser.Parse(requestMessage.Body) is not NSDictionary qualifierDictionary ||
                    !qualifierDictionary.ToDictionary().TryGetValue("qualifier", out NSObject? qualifierValue) ||
                    qualifierValue.ToObject() is not object[] { Length: > 0 and <= 2 } qualifiers)
                    throw new InvalidDataException("The AirPlay info qualifier list is invalid.");

                foreach (object qualifier in qualifiers)
                {
                    if (qualifier is not string name)
                        throw new InvalidDataException("An AirPlay info qualifier is invalid.");
                    AddTxtRecord(qualifiedInfo, name);
                }
            }

            await WritePlistResponseAsync(qualifiedInfo, responseMessage, cancellationToken);
            return;
        }

        Dictionary<string, object> infoDictionary = new()
        {
            { "features", Constants.FEATURES_VALUE },
            { "name", _airTunesConfig.ServiceName },
            {
                "displays",
                new List<Dictionary<string, object>>
                {
                    new()
                    {
                        { "primaryInputDevice", 1 },
                        { "rotation", false },
                        { "width", 1920 },
                        { "height", 1080 },
                        { "widthPhysical", false },
                        { "heightPhysical", false },
                        { "widthPixels", 1920.0 },
                        { "heightPixels", 1080.0 },
                        { "refreshRate", 1 / (float)Constants.MAX_FPS },
                        { "maxFPS", Constants.MAX_FPS },
                        { "features", 14 },
                        { "overscanned", false },
                        { "uuid", _identity.DisplayIdentifier.ToString("D") },
                    }
                }
            },
            {
                "audioFormats",
                new List<Dictionary<string, object>>
                {
                    {
                        new Dictionary<string, object>
                        {
                            { "type", 100 },
                            { "audioInputFormats", 67108860 },
                            { "audioOutputFormats", 67108860 }
                        }
                    },
                    {
                        new Dictionary<string, object>
                        {
                            { "type", 101 },
                            { "audioInputFormats", 67108860 },
                            { "audioOutputFormats", 67108860 }
                        }
                    }
                }
            },
            { "vv", 2 },
            { "initialVolume", 0.0 },
            { "statusFlags", _airTunesConfig.RequirePassword ? 132 : 68 },
            { "keepAliveLowPower", true },
            { "sourceVersion", Constants.AIPLAY_SERVICE_VERSION },
            { "pk", _publicKey },
            { "keepAliveSendStatsAsBody", true },
            { "deviceID", _identity.DeviceId },
            { "model", Constants.DEVICE_MODEL },
            {
                "audioLatencies",
                new List<Dictionary<string, object>>
                {
                    {
                        new Dictionary<string, object>
                        {
                            { "outputLatencyMicros", false },
                            { "type", 100 },
                            { "audioType", "default" },
                            { "inputLatencyMicros", false }
                        }
                    },
                    {
                        new Dictionary<string, object>
                        {
                            { "outputLatencyMicros", false },
                            { "type", 101 },
                            { "audioType", "default" },
                            { "inputLatencyMicros", false }
                        }
                    }
                }
            },
            { "macAddress", _identity.DeviceId },
            { "pi", _identity.PairingIdentifier.ToString("D") }
        };

        if (requestMessage.Path.Contains("txtAirPlay", StringComparison.Ordinal))
            AddTxtRecord(infoDictionary, "txtAirPlay");
        if (requestMessage.Path.Contains("txtRAOP", StringComparison.Ordinal))
            AddTxtRecord(infoDictionary, "txtRAOP");

        await WritePlistResponseAsync(infoDictionary, responseMessage, cancellationToken);
    }

    private void AddTxtRecord(Dictionary<string, object> response, string qualifier)
    {
        if (qualifier.Equals("txtAirPlay", StringComparison.Ordinal) && !response.ContainsKey(qualifier))
            response.Add(
                qualifier,
                AirPlayPublisher.PackTxtRecord(
                    AirPlayPublisher.GetAirPlayTxtProperties(_airTunesConfig, _identity)));
        else if (qualifier.Equals("txtRAOP", StringComparison.Ordinal) && !response.ContainsKey(qualifier))
            response.Add(
                qualifier,
                AirPlayPublisher.PackTxtRecord(
                    AirPlayPublisher.GetAirTunesTxtProperties(_airTunesConfig, _identity)));
    }

    private static async Task WritePlistResponseAsync(
        Dictionary<string, object> value,
        RtspResponseMessage responseMessage,
        CancellationToken cancellationToken)
    {
        NSObject binaryPlist = NSObject.Wrap(value);
        byte[] plistBytes = BinaryPropertyListWriter.WriteToArray(binaryPlist);
        responseMessage.Headers.Add("Content-Type", "application/x-apple-binary-plist");
        await responseMessage.WriteAsync(plistBytes, 0, plistBytes.Length, cancellationToken);
    }

    private async Task OnPostPairSetupRequested(RtspResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        // Return our 32 bytes public key
        responseMessage.Headers.Add("Content-Type", "application/octet-stream");
        await responseMessage.WriteAsync(_publicKey, 0, _publicKey.Length, cancellationToken);
    }

    private async Task OnPostPairVerifyRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        if (requestMessage.Body.Length != 68)
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        using var memoryStream = new MemoryStream(requestMessage.Body);
        using var reader = new BinaryReader(memoryStream);

        // Request: 68 bytes (the first 4 bytes are 01 00 00 00)
        // Client request packet remaining 64 bytes of content
        // 01 00 00 00 -> use 01 as flag to check type of verify
        // If flag is 1:
        // 32 bytes ecdh_their 
        // 32 bytes ed_their 
        // If flag is 0:
        // 64 bytes signature

        byte flag = reader.ReadByte();
        reader.ReadBytes(3);

        if (flag == 0)
        {
            if (_ecdhShared is null || _ecdhTheirs is null || _ecdhOurs is null || _edTheirs is null)
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            byte[] signature = reader.ReadBytes(64);

            using AESCTRBufferedCipher cipher = AESCTRBufferedCipher.CreateDefault(_ecdhShared);

            byte[] signatureBuffer = new byte[64];
            signatureBuffer = cipher.ProcessBytes(signatureBuffer);
            signatureBuffer = cipher.DoFinal(signature);

            byte[] messageBuffer = new byte[64];
            Array.Copy(_ecdhTheirs, 0, messageBuffer, 0, 32);
            Array.Copy(_ecdhOurs, 0, messageBuffer, 32, 32);

            var ed25519 = (Ed25519.Create("ed25519-sha512") as Ed25519)!;
            ed25519.FromPublicKey(_edTheirs);

            _pairVerified = ed25519.VerifyMessage(messageBuffer, signatureBuffer);

            if (_pairVerified)
                _logger?.PairVerified(_ActiveRemote ?? "unknown");
            else
            {
                responseMessage.Status = StatusCode.UNAUTHORIZED;
                _logger?.PairVerifyFailed(_ActiveRemote ?? "unknown");
            }
        }
        else if (flag == 1)
        {
            _ecdhTheirs = reader.ReadBytes(32);
            _edTheirs = reader.ReadBytes(32);

            _curve25519 = Curve25519.Create("curve25519-sha256");
            _ecdhOurs = _curve25519.GetPublicKey();
            _ecdhShared = _curve25519.GetSharedSecret(_ecdhTheirs);

            byte[] dataToSign = new byte[64];
            Array.Copy(_ecdhOurs, 0, dataToSign, 0, 32);
            Array.Copy(_ecdhTheirs, 0, dataToSign, 32, 32);

            byte[] signature = _ed25519.SignMessage(dataToSign);

            using AESCTRBufferedCipher cipher = AESCTRBufferedCipher.CreateDefault(_ecdhShared);

            byte[] encryptedSignature = cipher.DoFinal(signature);
            byte[] output = [.. _ecdhOurs, .. encryptedSignature];

            responseMessage.Headers.Add("Content-Type", "application/octet-stream");
            await responseMessage.WriteAsync(output, 0, output.Length, cancellationToken);
        }
        else
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            _logger?.UnknownFlagInPairVerify(flag);
        }
    }

    private async Task OnPostFpSetupRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        // If session is not paired, something gone wrong.
        if (_ecdhShared == null || !_pairVerified)
        {
            responseMessage.Status = StatusCode.UNAUTHORIZED;
            return;
        }

        if (requestMessage.Body.Length < 5 || requestMessage.Body[4] != 0x03)
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            _logger?.UnsupportedFairPlayVersion(requestMessage.Body.Length >= 5 ? requestMessage.Body[4] : (byte)0xff);
            return;
        }

        if (requestMessage.Body.Length == 16)
        {
            byte[][] replyMessage =
            [
                [0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x00,0x0f,0x9f,0x3f,0x9e,0x0a,0x25,0x21,0xdb,0xdf,0x31,0x2a,0xb2,0xbf,0xb2,0x9e,0x8d,0x23,0x2b,0x63,0x76,0xa8,0xc8,0x18,0x70,0x1d,0x22,0xae,0x93,0xd8,0x27,0x37,0xfe,0xaf,0x9d,0xb4,0xfd,0xf4,0x1c,0x2d,0xba,0x9d,0x1f,0x49,0xca,0xaa,0xbf,0x65,0x91,0xac,0x1f,0x7b,0xc6,0xf7,0xe0,0x66,0x3d,0x21,0xaf,0xe0,0x15,0x65,0x95,0x3e,0xab,0x81,0xf4,0x18,0xce,0xed,0x09,0x5a,0xdb,0x7c,0x3d,0x0e,0x25,0x49,0x09,0xa7,0x98,0x31,0xd4,0x9c,0x39,0x82,0x97,0x34,0x34,0xfa,0xcb,0x42,0xc6,0x3a,0x1c,0xd9,0x11,0xa6,0xfe,0x94,0x1a,0x8a,0x6d,0x4a,0x74,0x3b,0x46,0xc3,0xa7,0x64,0x9e,0x44,0xc7,0x89,0x55,0xe4,0x9d,0x81,0x55,0x00,0x95,0x49,0xc4,0xe2,0xf7,0xa3,0xf6,0xd5,0xba],
                [0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x01,0xcf,0x32,0xa2,0x57,0x14,0xb2,0x52,0x4f,0x8a,0xa0,0xad,0x7a,0xf1,0x64,0xe3,0x7b,0xcf,0x44,0x24,0xe2,0x00,0x04,0x7e,0xfc,0x0a,0xd6,0x7a,0xfc,0xd9,0x5d,0xed,0x1c,0x27,0x30,0xbb,0x59,0x1b,0x96,0x2e,0xd6,0x3a,0x9c,0x4d,0xed,0x88,0xba,0x8f,0xc7,0x8d,0xe6,0x4d,0x91,0xcc,0xfd,0x5c,0x7b,0x56,0xda,0x88,0xe3,0x1f,0x5c,0xce,0xaf,0xc7,0x43,0x19,0x95,0xa0,0x16,0x65,0xa5,0x4e,0x19,0x39,0xd2,0x5b,0x94,0xdb,0x64,0xb9,0xe4,0x5d,0x8d,0x06,0x3e,0x1e,0x6a,0xf0,0x7e,0x96,0x56,0x16,0x2b,0x0e,0xfa,0x40,0x42,0x75,0xea,0x5a,0x44,0xd9,0x59,0x1c,0x72,0x56,0xb9,0xfb,0xe6,0x51,0x38,0x98,0xb8,0x02,0x27,0x72,0x19,0x88,0x57,0x16,0x50,0x94,0x2a,0xd9,0x46,0x68,0x8a],
                [0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x02,0xc1,0x69,0xa3,0x52,0xee,0xed,0x35,0xb1,0x8c,0xdd,0x9c,0x58,0xd6,0x4f,0x16,0xc1,0x51,0x9a,0x89,0xeb,0x53,0x17,0xbd,0x0d,0x43,0x36,0xcd,0x68,0xf6,0x38,0xff,0x9d,0x01,0x6a,0x5b,0x52,0xb7,0xfa,0x92,0x16,0xb2,0xb6,0x54,0x82,0xc7,0x84,0x44,0x11,0x81,0x21,0xa2,0xc7,0xfe,0xd8,0x3d,0xb7,0x11,0x9e,0x91,0x82,0xaa,0xd7,0xd1,0x8c,0x70,0x63,0xe2,0xa4,0x57,0x55,0x59,0x10,0xaf,0x9e,0x0e,0xfc,0x76,0x34,0x7d,0x16,0x40,0x43,0x80,0x7f,0x58,0x1e,0xe4,0xfb,0xe4,0x2c,0xa9,0xde,0xdc,0x1b,0x5e,0xb2,0xa3,0xaa,0x3d,0x2e,0xcd,0x59,0xe7,0xee,0xe7,0x0b,0x36,0x29,0xf2,0x2a,0xfd,0x16,0x1d,0x87,0x73,0x53,0xdd,0xb9,0x9a,0xdc,0x8e,0x07,0x00,0x6e,0x56,0xf8,0x50,0xce],
                [0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x03,0x90,0x01,0xe1,0x72,0x7e,0x0f,0x57,0xf9,0xf5,0x88,0x0d,0xb1,0x04,0xa6,0x25,0x7a,0x23,0xf5,0xcf,0xff,0x1a,0xbb,0xe1,0xe9,0x30,0x45,0x25,0x1a,0xfb,0x97,0xeb,0x9f,0xc0,0x01,0x1e,0xbe,0x0f,0x3a,0x81,0xdf,0x5b,0x69,0x1d,0x76,0xac,0xb2,0xf7,0xa5,0xc7,0x08,0xe3,0xd3,0x28,0xf5,0x6b,0xb3,0x9d,0xbd,0xe5,0xf2,0x9c,0x8a,0x17,0xf4,0x81,0x48,0x7e,0x3a,0xe8,0x63,0xc6,0x78,0x32,0x54,0x22,0xe6,0xf7,0x8e,0x16,0x6d,0x18,0xaa,0x7f,0xd6,0x36,0x25,0x8b,0xce,0x28,0x72,0x6f,0x66,0x1f,0x73,0x88,0x93,0xce,0x44,0x31,0x1e,0x4b,0xe6,0xc0,0x53,0x51,0x93,0xe5,0xef,0x72,0xe8,0x68,0x62,0x33,0x72,0x9c,0x22,0x7d,0x82,0x0c,0x99,0x94,0x45,0xd8,0x92,0x46,0xc8,0xc3,0x59]
            ];

            // Get mode and send correct reply message
            // byte mode = requestMessage.Body[14];
            int mode = requestMessage.Body[14];
            if ((uint)mode >= (uint)replyMessage.Length)
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            byte[] output = replyMessage[mode];

            responseMessage.Headers.Add("Content-Type", "application/octet-stream");
            await responseMessage.WriteAsync(output, 0, output.Length, cancellationToken);
        }

        if (requestMessage.Body.Length == 164)
        {
            byte[] fpHeader = [0x46, 0x50, 0x4c, 0x59, 0x03, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x14];

            byte[] keyMsg = new byte[164];
            byte[] output = new byte[32];

            Array.Copy(requestMessage.Body, 0, keyMsg, 0, 164);
            _keyMsg = keyMsg;
            _logger?.FairPlaySetUp(_ActiveRemote ?? "unknown");

            byte[] data = [.. requestMessage.Body.Skip(144)];
            Array.Copy(fpHeader, 0, output, 0, 12);
            Array.Copy(data, 0, output, 12, 20);

            responseMessage.Headers.Add("Content-Type", "application/octet-stream");
            await responseMessage.WriteAsync(output, 0, output.Length, cancellationToken);
        }
        else if (requestMessage.Body.Length != 16)
        {
            responseMessage.Status = StatusCode.BADREQUEST;
        }
    }

    private async Task OnSetupRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        // If session is not ready, something gone wrong.
        if (_keyMsg == null || _ecdhShared == null || !_pairVerified)
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        if (requestMessage.Body.Length is 0 or > MaximumSetupBodyBytes ||
            PropertyListParser.Parse(requestMessage.Body) is not NSDictionary nsDict)
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        Dictionary<string, NSObject> plistDict = nsDict.ToDictionary();

        if (plistDict.TryGetValue("streams", out NSObject? nSObject))
        {
            if (_deviceSession is null ||
                nSObject.ToObject() is not object[] { Length: > 0 } streams ||
                !TryParseStreamSetups(
                    streams,
                    _deviceSession.AudioController is not null,
                    _deviceSession.MirrorController is not null,
                    out List<StreamSetup> streamSetups))
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            NSArray responseStreams = new();
            bool audioCreated = false;
            bool mirrorCreated = false;
            try
            {
                foreach (StreamSetup setup in streamSetups)
                {
                    if (setup is AudioStreamSetup audio)
                    {
                        _deviceSession.CreateAudioController(
                            audio.ControlPort,
                            audio.Format,
                            audio.LatencyMin,
                            audio.LatencyMax);
                        audioCreated = true;
                        _deviceSession.AudioController!.BeginConnectionWorkers();
                        responseStreams.Add(new NSDictionary
                        {
                            { "dataPort", (int)_deviceSession.AudioController.DataPort },
                            { "controlPort", (int)_deviceSession.AudioController.ControlPort },
                            { "type", 96 },
                        });
                    }
                    else if (setup is MirrorStreamSetup mirror)
                    {
                        _deviceSession.CreateMirrorController(mirror.StreamConnectionId);
                        mirrorCreated = true;
                        _deviceSession.MirrorController!.BeginConnectionWorkers();
                        responseStreams.Add(new NSDictionary
                        {
                            { "dataPort", (int)_deviceSession.MirrorController.DataPort },
                            { "type", 110 },
                        });
                    }
                }
            }
            catch
            {
                if (mirrorCreated)
                    _deviceSession.CloseMirrorController();
                if (audioCreated)
                    _deviceSession.CloseAudioController();
                throw;
            }

            NSDictionary keyValuePairs = new()
            {
                { "streams", responseStreams },
            };
            byte[] plistBytes = BinaryPropertyListWriter.WriteToArray(keyValuePairs);

            responseMessage.Headers.Add("Content-Type", "application/x-apple-binary-plist");
            await responseMessage.WriteAsync(plistBytes, 0, plistBytes.Length, cancellationToken);
        }
        else
        {
            if (!plistDict.TryGetValue("deviceID", out NSObject? deviceIdentifierValue))
                plistDict.TryGetValue("macAddress", out deviceIdentifierValue);

            if (_deviceSession is not null ||
                !plistDict.TryGetValue("eiv", out NSObject? eivValue) || eivValue.ToObject() is not byte[] { Length: 16 } aesIv ||
                !plistDict.TryGetValue("ekey", out NSObject? ekeyValue) || ekeyValue.ToObject() is not byte[] { Length: 72 } encryptedAesKey ||
                !plistDict.TryGetValue("timingPort", out NSObject? timingPortValue) || !TryGetUInt16(timingPortValue.ToObject(), out ushort timingPort) ||
                !plistDict.TryGetValue("name", out NSObject? nameValue) || nameValue.ToObject() is not string { Length: > 0 and <= 256 } name ||
                deviceIdentifierValue?.ToObject() is not string { Length: > 0 and <= 64 } deviceIdentifier)
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            plistDict.TryGetValue("model", out NSObject? modelValue);
            plistDict.TryGetValue("isScreenMirroringSession", out NSObject? isScreenMirroringSessionValue);

            string? model = modelValue?.ToObject() as string;
            if (model?.Length > 256)
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            DeviceSession deviceSession = new(
                aesIv,
                _ecdhShared,
                timingPort,
                _endPoint.Address,
                _loggerFactory?.CreateLogger<DeviceSession>())
            {
                DeviceMacAddress = deviceIdentifier,
                DeviceDisplayName = name,
                DeviceModel = model,
                DacpId = _DACPID ?? string.Empty,
                ActiveRemote = _ActiveRemote ?? string.Empty,
                IsMirrorSession = isScreenMirroringSessionValue?.ToObject() is bool isScreenMirroringSession && isScreenMirroringSession
            };

            try
            {
                deviceSession.DecrypteAesKey(_keyMsg, encryptedAesKey);
                deviceSession.BeginTiming();
            }
            catch
            {
                deviceSession.Dispose();
                throw;
            }

            _deviceSession = deviceSession;
            deviceSession.DisconnectRequested += OnDeviceSessionDisconnectRequested;

            SessionPaired?.Invoke(this, deviceSession);

            NSDictionary timingResponse = new()
            {
                { "timingPort", (int)deviceSession.TimingPort },
                { "eventPort", 0 },
            };
            byte[] responseBytes = BinaryPropertyListWriter.WriteToArray(timingResponse);
            responseMessage.Headers.Add("Content-Type", "application/x-apple-binary-plist");
            await responseMessage.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
        }
    }

    private static bool TryGetInt32(object? value, out int result)
    {
        switch (value)
        {
            case byte number: result = number; return true;
            case sbyte number: result = number; return true;
            case short number: result = number; return true;
            case ushort number: result = number; return true;
            case int number: result = number; return true;
            case uint number when number <= int.MaxValue: result = (int)number; return true;
            case long number when number is >= int.MinValue and <= int.MaxValue: result = (int)number; return true;
            case ulong number when number <= int.MaxValue: result = (int)number; return true;
            default: result = 0; return false;
        }
    }

    private static bool TryGetUInt16(object? value, out ushort result)
    {
        if (TryGetInt32(value, out int number) && number is > 0 and <= ushort.MaxValue)
        {
            result = (ushort)number;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryGetStreamConnectionId(object? value, out string result)
    {
        switch (value)
        {
            case ulong number:
                result = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case long number:
                result = unchecked((ulong)number).ToString(CultureInfo.InvariantCulture);
                return true;
            case uint number:
                result = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case int number:
                result = unchecked((uint)number).ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    internal static bool TryParseStreamSetups(
        object[] streams,
        bool hasAudioController,
        bool hasMirrorController,
        out List<StreamSetup> setups)
    {
        setups = [];
        if (streams.Length is 0 or > 2)
            return false;

        List<StreamSetup> parsedSetups = [];
        bool includesAudio = false;
        bool includesMirror = false;
        foreach (object item in streams)
        {
            if (item is not Dictionary<string, object> stream ||
                !stream.TryGetValue("type", out object? typeValue) ||
                !TryGetInt32(typeValue, out int type))
                return false;

            if (type == 96)
            {
                if (hasAudioController || includesAudio ||
                    !stream.TryGetValue("audioFormat", out object? formatValue) ||
                    !TryGetInt32(formatValue, out int formatNumber) ||
                    !IsSupportedAudioFormat(formatNumber) ||
                    !stream.TryGetValue("controlPort", out object? controlPortValue) ||
                    !TryGetUInt16(controlPortValue, out ushort controlPort) ||
                    !TryGetOptionalNonNegativeInt32(stream, "latencyMin", out int? latencyMin) ||
                    !TryGetOptionalNonNegativeInt32(stream, "latencyMax", out int? latencyMax))
                    return false;

                includesAudio = true;
                parsedSetups.Add(new AudioStreamSetup((AudioFormat)formatNumber, controlPort, latencyMin, latencyMax));
            }
            else if (type == 110)
            {
                if (hasMirrorController || includesMirror ||
                    !stream.TryGetValue("streamConnectionID", out object? connectionIdValue) ||
                    !TryGetStreamConnectionId(connectionIdValue, out string streamConnectionId))
                    return false;

                includesMirror = true;
                parsedSetups.Add(new MirrorStreamSetup(streamConnectionId));
            }
            else
            {
                return false;
            }
        }

        setups = parsedSetups;
        return true;
    }

    private static bool TryGetOptionalNonNegativeInt32(
        Dictionary<string, object> dictionary,
        string key,
        out int? result)
    {
        if (!dictionary.TryGetValue(key, out object? value))
        {
            result = null;
            return true;
        }

        if (TryGetInt32(value, out int number) && number >= 0)
        {
            result = number;
            return true;
        }

        result = null;
        return false;
    }

    private static bool IsSupportedAudioFormat(int value)
        => value is (int)AudioFormat.PCM or (int)AudioFormat.ALAC or
            (int)AudioFormat.AAC or (int)AudioFormat.AAC_ELD;

    internal abstract record StreamSetup;

    internal sealed record AudioStreamSetup(
        AudioFormat Format,
        ushort ControlPort,
        int? LatencyMin,
        int? LatencyMax) : StreamSetup;

    internal sealed record MirrorStreamSetup(string StreamConnectionId) : StreamSetup;

    private static void OnOptionsRequested(RtspResponseMessage responseMessage)
        => responseMessage.Headers["Public"] = [SupportedMethods];

    private async Task OnGetParameterRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        if (requestMessage.Body.Length > MaximumTextParameterBytes || !IsAsciiParameterBody(requestMessage.Body))
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        string[] parameters = Encoding.ASCII.GetString(requestMessage.Body)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parameters.Any(parameter => parameter.Equals("volume", StringComparison.OrdinalIgnoreCase)))
        {
            // The volume is a float value representing the audio attenuation in dB.
            // Then it goes from –30 to 0.
            // A value of –144 means the audio is muted.
            double volume = ((_deviceSession?.Volume ?? 100) / 100 * 30) - 30;
            byte[] output = Encoding.ASCII.GetBytes(
                $"volume: {volume.ToString("0.000000", CultureInfo.InvariantCulture)}\r\n");

            responseMessage.Headers.Add("Content-Type", "text/parameters");
            await responseMessage.WriteAsync(output, 0, output.Length, cancellationToken);
        }
    }

    private static void OnRecordRequested(RtspResponseMessage responseMessage)
    {
        responseMessage.Headers["Audio-Latency"] = ["0"];
        responseMessage.Headers["Audio-Jack-Status"] = ["connected; type=digital"];
    }

    private static Task OnPostFeedbackRequested()
    {
        // return nothing
        return Task.CompletedTask;
    }

    private void OnFlushRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage)
    {
        int nextSeq = -1;

        if (requestMessage.Headers.TryGetSingleValue("RTP-Info", out string? rtpInfo) &&
            !TryParseRtpSequence(rtpInfo, out nextSeq))
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        _deviceSession?.AudioController?.Flush(nextSeq);
    }

    private Task OnTeardownRequested(RtspRequestMessage requestMessage)
    {
        if (!TryParseTeardown(
            requestMessage.Body,
            out bool closeAudio,
            out bool closeMirror,
            out bool closeSession))
            throw new InvalidDataException("The sender provided an invalid TEARDOWN body.");

        if (closeAudio)
            _deviceSession?.CloseAudioController();
        if (closeMirror)
            _deviceSession?.CloseMirrorController();
        if (closeSession)
        {
            _disconnectRequested = true;
        }

        return Task.CompletedTask;
    }

    private void OnSetParameterRequested(RtspRequestMessage requestMessage, RtspResponseMessage responseMessage)
    {
        if (!requestMessage.Headers.TryGetSingleValue("Content-Type", out string? contentType))
        {
            responseMessage.Status = StatusCode.BADREQUEST;
            return;
        }

        if (contentType.Equals("text/parameters", StringComparison.OrdinalIgnoreCase))
        {
            if (requestMessage.Body.Length is 0 or > MaximumTextParameterBytes ||
                !IsAsciiParameterBody(requestMessage.Body))
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            string[] lines = Encoding.ASCII.GetString(requestMessage.Body)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            double? requestedVolume = null;
            MediaProgressInfo? requestedProgress = null;
            foreach (string line in lines)
            {
                int separator = line.IndexOf(':');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    responseMessage.Status = StatusCode.BADREQUEST;
                    return;
                }

                string key = line[..separator].Trim();
                ReadOnlySpan<char> value = line.AsSpan(separator + 1).Trim();
                if (key.Equals("volume", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseVolume(value, out double parsedVolume))
                    {
                        responseMessage.Status = StatusCode.BADREQUEST;
                        return;
                    }
                    requestedVolume = parsedVolume;
                }
                else if (key.Equals("progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseProgress(value, out MediaProgressInfo parsedProgress))
                    {
                        responseMessage.Status = StatusCode.BADREQUEST;
                        return;
                    }
                    requestedProgress = parsedProgress;
                }
            }

            if (requestedVolume is double volumeToApply)
                _deviceSession?.RemoteSetVolume(volumeToApply);
            if (requestedProgress is MediaProgressInfo progressToApply)
                _deviceSession?.RemoteSetProgress(progressToApply);
        }
        else if (contentType.Equals("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase))
        {
            if (requestMessage.Body.Length is 0 or > MaximumMetadataBytes)
            {
                responseMessage.Status = StatusCode.BADREQUEST;
                return;
            }

            DMapTagged dmap = new();
            Dictionary<string, object> output = dmap.Decode(requestMessage.Body);

            if (!output.TryGetValue("minm", out object? minm) || minm is not string { Length: > 0 and <= 1024 } name)
                return;
            output.TryGetValue("asar", out var asar);
            output.TryGetValue("asal", out var asal);

            _deviceSession?.RemoteSetWorkInfo(new(
                name,
                asar is string { Length: <= 1024 } artist ? artist : null,
                asal is string { Length: <= 1024 } album ? album : null));
        }
        else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            _deviceSession?.RemoteSetCover(requestMessage.Body);
    }

    internal static bool TryParseRtpSequence(string value, out int sequence)
    {
        sequence = -1;
        foreach (string segment in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!segment.StartsWith("seq=", StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(
                    segment.AsSpan(4),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sequence) &&
                sequence is >= 0 and <= ushort.MaxValue;
        }

        return false;
    }

    internal static bool TryParseVolume(ReadOnlySpan<char> value, out double volume)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out volume) &&
            double.IsFinite(volume) && volume is >= -144 and <= 0)
            return true;

        volume = 0;
        return false;
    }

    internal static bool TryParseProgress(ReadOnlySpan<char> value, out MediaProgressInfo progress)
    {
        progress = default;
        int firstSeparator = value.IndexOf('/');
        int secondSeparator = firstSeparator < 0 ? -1 : value[(firstSeparator + 1)..].IndexOf('/');
        if (firstSeparator <= 0 || secondSeparator < 0)
            return false;
        secondSeparator += firstSeparator + 1;
        if (secondSeparator >= value.Length - 1 || value[(secondSeparator + 1)..].Contains('/'))
            return false;

        if (!long.TryParse(value[..firstSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
            !long.TryParse(value[(firstSeparator + 1)..secondSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out long current) ||
            !long.TryParse(value[(secondSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long end) ||
            start < 0 || current < start || end < current)
            return false;

        double durationSeconds = (end - start) / 44_100d;
        double positionSeconds = (current - start) / 44_100d;
        if (durationSeconds > TimeSpan.MaxValue.TotalSeconds)
            return false;

        progress = new(TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(positionSeconds));
        return true;
    }

    internal static bool TryParseTeardown(
        byte[] body,
        out bool closeAudio,
        out bool closeMirror,
        out bool closeSession)
    {
        closeAudio = false;
        closeMirror = false;
        closeSession = false;
        if (body.Length == 0)
        {
            closeAudio = closeMirror = closeSession = true;
            return true;
        }
        if (body.Length > MaximumSetupBodyBytes)
            return false;

        try
        {
            if (PropertyListParser.Parse(body) is not NSDictionary dictionary)
                return false;
            Dictionary<string, NSObject> values = dictionary.ToDictionary();
            if (!values.TryGetValue("streams", out NSObject? streamsValue))
            {
                closeAudio = closeMirror = closeSession = true;
                return true;
            }
            if (streamsValue.ToObject() is not object[] streams)
                return false;
            if (streams.Length == 0)
            {
                closeAudio = closeMirror = closeSession = true;
                return true;
            }
            if (streams.Length > 2)
                return false;

            bool parsedAudio = false;
            bool parsedMirror = false;
            foreach (object item in streams)
            {
                if (item is not Dictionary<string, object> stream ||
                    !stream.TryGetValue("type", out object? typeValue) ||
                    !TryGetInt32(typeValue, out int type))
                    return false;

                if (type == 96 && !parsedAudio)
                    parsedAudio = true;
                else if (type == 110 && !parsedMirror)
                    parsedMirror = true;
                else
                    return false;
            }

            closeAudio = parsedAudio;
            closeMirror = parsedMirror;
            return true;
        }
        catch
        {
            closeAudio = closeMirror = closeSession = false;
            return false;
        }
    }

    private static bool IsAsciiParameterBody(ReadOnlySpan<byte> body)
    {
        foreach (byte value in body)
        {
            if (value is not (>= 0x20 and <= 0x7e) and not (byte)'\r' and not (byte)'\n' and not (byte)'\t')
                return false;
        }
        return true;
    }
}

internal static partial class RtspConnectionLoggers
{
    [LoggerMessage(LogLevel.Error, "Unknown flag in PairVerify process: [\"flag\": (byte){flag}]")]
    public static partial void UnknownFlagInPairVerify(this ILogger logger, byte flag);

    [LoggerMessage(LogLevel.Error, "Pair Verify Failed for [{activeRemote}]")]
    public static partial void PairVerifyFailed(this ILogger logger, string activeRemote);

    [LoggerMessage(LogLevel.Information, "Pair Verified for [{activeRemote}]")]
    public static partial void PairVerified(this ILogger logger, string activeRemote);

    [LoggerMessage(LogLevel.Error, "Unsupported fairplay version: [\"body[4]\": (byte){value}]")]
    public static partial void UnsupportedFairPlayVersion(this ILogger logger, byte value);

    [LoggerMessage(LogLevel.Information, "FairPlay is setup for [{activeRemote}]")]
    public static partial void FairPlaySetUp(this ILogger logger, string activeRemote);
}
