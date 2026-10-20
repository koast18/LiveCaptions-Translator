using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>What the device-registration flow produces and what the ASR socket needs.</summary>
    public sealed record DoubaoCredentials(string DeviceId, string AppKey, string InstallId, string Cdid)
    {
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(AppKey);
    }

    /// <summary>
    /// The fixed part of the device fingerprint plus the request shaping for the two endpoints.
    /// <para>
    /// Everything here is pure so the wire format can be asserted in unit tests; the network part
    /// lives in <see cref="DoubaoDeviceRegistrar"/>.
    /// </para>
    /// </summary>
    public static class DoubaoDeviceProfile
    {
        public const string RegisterUrl = "https://log.snssdk.com/service/2/device_register/";

        public const string SettingsUrl = "https://is.snssdk.com/service/settings/v3/";

        public const int Aid = 401734;

        public const string AppName = "oime";

        public const int VersionCode = 100102018;

        public const string VersionName = "1.1.2";

        public const string Channel = "official";

        public const string Package = "com.bytedance.android.doubaoime";

        public const string DevicePlatform = "android";

        public const string OsType = "android";

        public const string OsApi = "34";

        public const string OsVersion = "16";

        public const string DeviceType = "Pixel 7 Pro";

        public const string DeviceBrand = "google";

        public const string DeviceModel = "Pixel 7 Pro";

        public const string Resolution = "1080*2400";

        public const string Dpi = "420";

        public const string Language = "zh";

        public const int Timezone = 8;

        public const string Access = "wifi";

        public const string Rom = "UP1A.231005.007";

        public const string RomVersion = "UP1A.231005.007";

        public const string Region = "CN";

        public const string TzName = "Asia/Shanghai";

        public const int TzOffset = 28800;

        public const string SimRegion = "cn";

        public const string CarrierRegion = "cn";

        public const string CpuAbi = "arm64-v8a";

        public const string BuildSerial = "unknown";

        /// <summary>The mobile UA the IME uses; the endpoints reject browser-like agents.</summary>
        public const string UserAgent =
            "com.bytedance.android.doubaoime/100102018 " +
            "(Linux; U; Android 16; en_US; Pixel 7 Pro; " +
            "Build/BP2A.250605.031.A2; Cronet/TTNetVersion:94cf429a 2025-11-17 " +
            "QuicVersion:1f89f732 2025-05-08)";

        /// <summary>
        /// The settings endpoint always posts this exact body and signs it with its MD5, upper case.
        /// </summary>
        public const string SettingsBody = "body=null";

        /// <summary>Builds the JSON body of <c>device_register</c>.</summary>
        public static string BuildRegisterBody(
            string cdid,
            string openudid,
            string clientudid,
            long nowUnixMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cdid);
            ArgumentException.ThrowIfNullOrWhiteSpace(openudid);
            ArgumentException.ThrowIfNullOrWhiteSpace(clientudid);

            var builder = new StringBuilder(1024);
            builder.Append("{\"magic_tag\":\"ss_app_log\",\"header\":{");

            bool first = true;

            void Add(string name, string jsonValue)
            {
                if (!first)
                    builder.Append(',');

                first = false;
                builder.Append('"').Append(name).Append("\":").Append(jsonValue);
            }

            Add("device_id", "0");
            Add("install_id", "0");
            Add("aid", Aid.ToString(CultureInfo.InvariantCulture));
            Add("app_name", Quote(AppName));
            Add("version_code", VersionCode.ToString(CultureInfo.InvariantCulture));
            Add("version_name", Quote(VersionName));
            Add("manifest_version_code", VersionCode.ToString(CultureInfo.InvariantCulture));
            Add("update_version_code", VersionCode.ToString(CultureInfo.InvariantCulture));
            Add("channel", Quote(Channel));
            Add("package", Quote(Package));
            Add("device_platform", Quote(DevicePlatform));
            Add("os", Quote(OsType));
            Add("os_api", Quote(OsApi));
            Add("os_version", Quote(OsVersion));
            Add("device_type", Quote(DeviceType));
            Add("device_brand", Quote(DeviceBrand));
            Add("device_model", Quote(DeviceModel));
            Add("resolution", Quote(Resolution));
            Add("dpi", Quote(Dpi));
            Add("language", Quote(Language));
            Add("timezone", Timezone.ToString(CultureInfo.InvariantCulture));
            Add("access", Quote(Access));
            Add("rom", Quote(Rom));
            Add("rom_version", Quote(RomVersion));
            Add("openudid", Quote(openudid));
            Add("clientudid", Quote(clientudid));
            Add("cdid", Quote(cdid));
            Add("region", Quote(Region));
            Add("tz_name", Quote(TzName));
            Add("tz_offset", TzOffset.ToString(CultureInfo.InvariantCulture));
            Add("sim_region", Quote(SimRegion));
            Add("carrier_region", Quote(CarrierRegion));
            Add("cpu_abi", Quote(CpuAbi));
            Add("build_serial", Quote(BuildSerial));
            Add("not_request_sender", "0");
            Add("sig_hash", Quote(string.Empty));
            Add("google_aid", Quote(string.Empty));
            Add("mc", Quote(string.Empty));
            Add("serial_number", Quote(string.Empty));

            builder.Append("},\"_gen_time\":").Append(nowUnixMs.ToString(CultureInfo.InvariantCulture)).Append('}');
            return builder.ToString();
        }

        /// <summary>Query string of <c>device_register</c>.</summary>
        public static string BuildRegisterQuery(string cdid, long nowUnixMs)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("device_platform", DevicePlatform),
                new("os", OsType),
                new("ssmix", "a"),
                new("_rticket", nowUnixMs.ToString(CultureInfo.InvariantCulture)),
                new("cdid", cdid),
                new("channel", Channel),
                new("aid", Aid.ToString(CultureInfo.InvariantCulture)),
                new("app_name", AppName),
                new("version_code", VersionCode.ToString(CultureInfo.InvariantCulture)),
                new("version_name", VersionName),
                new("manifest_version_code", VersionCode.ToString(CultureInfo.InvariantCulture)),
                new("update_version_code", VersionCode.ToString(CultureInfo.InvariantCulture)),
                new("resolution", Resolution),
                new("dpi", Dpi),
                new("device_type", DeviceType),
                new("device_brand", DeviceBrand),
                new("language", Language),
                new("os_api", OsApi),
                new("os_version", OsVersion),
                new("ac", Access),
            };

            return ToQuery(parameters);
        }

        /// <summary>Query string of the settings (token) endpoint.</summary>
        public static string BuildSettingsQuery(string deviceId, string cdid, long nowUnixMs)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("device_platform", DevicePlatform),
                new("os", OsType),
                new("ssmix", "a"),
                new("_rticket", nowUnixMs.ToString(CultureInfo.InvariantCulture)),
                new("cdid", cdid),
                new("channel", Channel),
                new("aid", Aid.ToString(CultureInfo.InvariantCulture)),
                new("app_name", AppName),
                new("version_code", VersionCode.ToString(CultureInfo.InvariantCulture)),
                new("version_name", VersionName),
                new("device_id", deviceId),
            };

            return ToQuery(parameters);
        }

        /// <summary>Upper-case MD5 of the request body, sent as <c>x-ss-stub</c>.</summary>
        public static string StubHash(string body)
        {
            byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(body));
            return Convert.ToHexString(digest);
        }

        /// <summary>64 random hex characters, the IME's openudid.</summary>
        public static string NewOpenUdid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        public static string NewUdid() => Guid.NewGuid().ToString();

        private static string ToQuery(IEnumerable<KeyValuePair<string, string>> parameters) =>
            string.Join(
                "&",
                parameters.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        /// <summary>Minimal JSON string literal; every value here is plain ASCII.</summary>
        private static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    default:
                        if (character < 0x20)
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }

    /// <summary>Pulls the fields we need out of the two endpoint responses. Pure, so it is testable.</summary>
    public static class DoubaoRegistrationParser
    {
        /// <summary>Reads <c>device_id_str</c> or <c>device_id</c>; throws when the response has neither.</summary>
        public static string ParseDeviceId(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryReadScalar(root, "device_id_str", out string? deviceId) ||
                TryReadScalar(root, "device_id", out deviceId))
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                    return deviceId!;
            }

            throw new DoubaoRegistrationException(
                "device_register 响应缺少 device_id：" + Truncate(json));
        }

        public static string ParseInstallId(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryReadScalar(root, "install_id_str", out string? installId) ||
                TryReadScalar(root, "install_id", out installId))
            {
                return installId ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>Reads <c>data.settings.asr_config.app_key</c>, the ASR token.</summary>
        public static string ParseAppKey(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("settings", out var settings) &&
                settings.ValueKind == JsonValueKind.Object &&
                settings.TryGetProperty("asr_config", out var asr) &&
                asr.ValueKind == JsonValueKind.Object &&
                asr.TryGetProperty("app_key", out var appKey) &&
                appKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(appKey.GetString()))
            {
                return appKey.GetString()!;
            }

            throw new DoubaoRegistrationException(
                "settings 响应缺少 data.settings.asr_config.app_key：" + Truncate(json));
        }

        private static bool TryReadScalar(JsonElement root, string name, out string? value)
        {
            value = null;

            if (!root.TryGetProperty(name, out var element))
                return false;

            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null,
            };

            return value is not null;
        }

        private static string Truncate(string json) =>
            json.Length <= 200 ? json : json[..200] + "…";
    }

    /// <summary>Raised when registration or token exchange fails; never crashes the caller.</summary>
    public sealed class DoubaoRegistrationException : Exception
    {
        public DoubaoRegistrationException(string message)
            : base(message)
        {
        }

        public DoubaoRegistrationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
