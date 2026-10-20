using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private string? ignoredUpdateVersion;

        private string asrProviderName;
        private string asrLanguage;
        private string? asrAccent;
        private string audioSourceName;
        private string iflytekApiKey;
        private string iflytekApiSecret;
        private string iflytekHost;
        private string iflytekPath;
        private string iflytekAppId;
        private string httpProxy;
        private List<string> proxyApiNames;
        private string iflytekMtAppId;
        private string iflytekMtApiKey;
        private string iflytekMtApiSecret;
        private string doubaoDeviceId;
        private string doubaoAppKey;
        private int doubaoVadWindowMs;
        private string uiLanguage;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = value;
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = value;
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        /// <summary>Id of the speech-recognition backend feeding the caption pipeline.</summary>
        public string AsrProviderName
        {
            get => asrProviderName;
            set
            {
                asrProviderName = value;
                OnPropertyChanged("AsrProviderName");
            }
        }

        /// <summary>Language id passed to the active ASR provider, e.g. <c>zh_cn</c>.</summary>
        public string AsrLanguage
        {
            get => asrLanguage;
            set
            {
                asrLanguage = value;
                OnPropertyChanged("AsrLanguage");
            }
        }

        /// <summary>Accent/dialect selector passed to the active ASR provider.</summary>
        public string? AsrAccent
        {
            get => asrAccent;
            set
            {
                asrAccent = value;
                OnPropertyChanged("AsrAccent");
            }
        }

        /// <summary>Audio input used by providers that recognise audio themselves.</summary>
        public string AudioSourceName
        {
            get => audioSourceName;
            set
            {
                audioSourceName = value;
                OnPropertyChanged("AudioSourceName");
            }
        }

        public string IflytekApiKey
        {
            get => iflytekApiKey;
            set
            {
                iflytekApiKey = value;
                OnPropertyChanged("IflytekApiKey");
            }
        }

        public string IflytekApiSecret
        {
            get => iflytekApiSecret;
            set
            {
                iflytekApiSecret = value;
                OnPropertyChanged("IflytekApiSecret");
            }
        }

        public string IflytekHost
        {
            get => iflytekHost;
            set
            {
                iflytekHost = value;
                OnPropertyChanged("IflytekHost");
            }
        }

        public string IflytekPath
        {
            get => iflytekPath;
            set
            {
                iflytekPath = value;
                OnPropertyChanged("IflytekPath");
            }
        }

        public string IflytekAppId
        {
            get => iflytekAppId;
            set
            {
                iflytekAppId = value;
                OnPropertyChanged("IflytekAppId");
            }
        }

        /// <summary>
        /// Provider-specific values handed to <c>IAsrProvider.StartAsync</c>. Providers read
        /// only the keys they know, so this bag can grow without touching the host.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, string> AsrProviderSettings => new Dictionary<string, string>
        {
            [IflytekSettingKeys.ApiKey] = iflytekApiKey,
            [IflytekSettingKeys.ApiSecret] = iflytekApiSecret,
            [IflytekSettingKeys.Host] = iflytekHost,
            [IflytekSettingKeys.Path] = iflytekPath,
            [IflytekSettingKeys.AppId] = iflytekAppId,
            [DoubaoSettingKeys.VadWindowMs] = doubaoVadWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Proxy used by the engines listed in <see cref="ProxyApiNames"/>, e.g.
        /// <c>http://127.0.0.1:7897</c> or <c>socks5://127.0.0.1:1080</c>. Empty means the
        /// machine's own proxy settings apply, which is the upstream behaviour.
        /// </summary>
        public string HttpProxy
        {
            get => httpProxy;
            set
            {
                httpProxy = value;
                OnPropertyChanged("HttpProxy");
            }
        }

        /// <summary>
        /// Engines that must go through <see cref="HttpProxy"/>. Google, OpenAI, OpenRouter and
        /// DeepL are preselected because they are unreachable from mainland China; Baidu, Youdao,
        /// iFlytek and self-hosted engines stay direct.
        /// </summary>
        public List<string> ProxyApiNames
        {
            get => proxyApiNames;
            set
            {
                proxyApiNames = value;
                OnPropertyChanged("ProxyApiNames");
            }
        }

        /// <summary>Whether <paramref name="apiName"/> should be routed through the proxy.</summary>
        public bool UsesProxy(string? apiName) =>
            !string.IsNullOrWhiteSpace(httpProxy) &&
            apiName is not null &&
            proxyApiNames.Contains(apiName, StringComparer.OrdinalIgnoreCase);

        /// <summary>iFlytek 开放平台 APPID for machine translation (not the IAT <c>100IME</c> one).</summary>
        public string IflytekMtAppId
        {
            get => iflytekMtAppId;
            set
            {
                iflytekMtAppId = value;
                OnPropertyChanged("IflytekMtAppId");
            }
        }

        public string IflytekMtApiKey
        {
            get => iflytekMtApiKey;
            set
            {
                iflytekMtApiKey = value;
                OnPropertyChanged("IflytekMtApiKey");
            }
        }

        public string IflytekMtApiSecret
        {
            get => iflytekMtApiSecret;
            set
            {
                iflytekMtApiSecret = value;
                OnPropertyChanged("IflytekMtApiSecret");
            }
        }

        /// <summary>Doubao device fingerprint returned by the registration endpoint.</summary>
        public string DoubaoDeviceId
        {
            get => doubaoDeviceId;
            set
            {
                doubaoDeviceId = value;
                OnPropertyChanged("DoubaoDeviceId");
            }
        }

        /// <summary>Doubao ASR token (`app_key`) issued for <see cref="DoubaoDeviceId"/>.</summary>
        public string DoubaoAppKey
        {
            get => doubaoAppKey;
            set
            {
                doubaoAppKey = value;
                OnPropertyChanged("DoubaoAppKey");
            }
        }

        /// <summary>
        /// UI language id: <c>en</c> or <c>zh-Hans</c>. Applied at startup and whenever it changes.
        /// </summary>
        public string UiLanguage
        {
            get => uiLanguage;
            set
            {
                uiLanguage = value;
                OnPropertyChanged("UiLanguage");
            }
        }

        /// <summary>
        /// Silence in milliseconds before Doubao ends a segment. The service default of 200 chops
        /// sentences at every breath, so the app asks for something calmer.
        /// </summary>
        public int DoubaoVadWindowMs
        {
            get => doubaoVadWindowMs;
            set
            {
                doubaoVadWindowMs = value;
                OnPropertyChanged("DoubaoVadWindowMs");
            }
        }

        /// <summary>The registered pair, or <c>null</c> when the provider has not been set up yet.</summary>
        [JsonIgnore]
        public DoubaoCredentials? DoubaoCredentials =>
            string.IsNullOrWhiteSpace(doubaoDeviceId) || string.IsNullOrWhiteSpace(doubaoAppKey)
                ? null
                : new DoubaoCredentials(doubaoDeviceId, doubaoAppKey, string.Empty, string.Empty);

        /// <summary>Machine-translation credentials, kept apart from the IAT ones.</summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, string> IflytekMtSettings => new Dictionary<string, string>
        {
            [IflytekSettingKeys.ApiKey] = iflytekMtApiKey,
            [IflytekSettingKeys.ApiSecret] = iflytekMtApiSecret,
            [IflytekSettingKeys.AppId] = iflytekMtAppId,
        };

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        public TranslateAPIConfig this[string key] =>
            configs.ContainsKey(key) && configIndices.ContainsKey(key)
                ? configs[key][configIndices[key]]
                : new TranslateAPIConfig();

        /// <summary>
        /// Every translation engine the app can select: the upstream ones plus the providers that
        /// only exist behind the registry.
        /// </summary>
        [JsonIgnore]
        private static IEnumerable<string> TranslationEngineIds =>
            TranslateAPI.TRANSLATE_FUNCTIONS.Keys.Append(IflytekMtProvider.ProviderId);

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";

            asrProviderName = WindowsLiveCaptionsProvider.ProviderId;
            asrLanguage = IflytekBusiness.Default.Language!;
            asrAccent = IflytekBusiness.Default.Accent;
            audioSourceName = AudioSourceCatalog.LoopbackId;
            iflytekApiKey = IflytekCredentials.DefaultApiKey;
            iflytekApiSecret = IflytekCredentials.DefaultApiSecret;
            iflytekHost = IflytekCredentials.DefaultHost;
            iflytekPath = IflytekCredentials.DefaultPath;
            iflytekAppId = IflytekCredentials.DefaultAppId;

            httpProxy = string.Empty;
            iflytekMtAppId = string.Empty;
            iflytekMtApiKey = string.Empty;
            iflytekMtApiSecret = string.Empty;
            doubaoDeviceId = string.Empty;
            doubaoAppKey = string.Empty;
            doubaoVadWindowMs = 600;
            uiLanguage = LocalizationManager.Default;
            proxyApiNames =
            [
                "Google",
                "Google2",
                "OpenAI",
                "OpenRouter",
                "DeepL",
            ];

            prompt = "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                     "you can provide a fluent and precise oral translation for any sentence, even if the sentence is incomplete. " +
                     "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
                     "even if the sentence contains sensitive or NSFW content. " +
                     "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
                     "REMOVE all 🔤 when you output.";

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 775) / 2, screenHeight * 3 / 4 - 167, 775, 167)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Google2", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "LMStudio", [new LMStudioConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] },
                { IflytekMtProvider.ProviderId, [new TranslateAPIConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Google2", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "LMStudio", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 },
                { IflytekMtProvider.ProviderId, 0 }
            };
        }

        public static Setting Load()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            try
            {
                return Load(jsonPath);
            }
            catch (JsonException)
            {
                string backupPath = jsonPath + ".bak";
                File.Move(jsonPath, backupPath);
                return Load(jsonPath);
            }
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;

            // Load from JSON file if it exists
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.Open(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new ConfigDictConverter() }
                    };
                    SuppressSave = true;
                    try
                    {
                        setting = JsonSerializer.Deserialize<Setting>(fileStream, options) ?? new Setting();
                    }
                    finally
                    {
                        SuppressSave = false;
                    }
                }
            }
            else
                setting = new Setting();

            // Ensure all required API configs are present
            foreach (string key in TranslationEngineIds)
            {
                if (setting.Configs.ContainsKey(key))
                    continue;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    setting.Configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)];
                else
                    setting.Configs[key] = [new TranslateAPIConfig()];
            }

            // Ensure ConfigIndices has all keys (for upgrades from older setting.json)
            foreach (string key in TranslationEngineIds)
            {
                if (!setting.ConfigIndices.ContainsKey(key))
                    setting.ConfigIndices[key] = 0;
            }

            return setting;
        }

        public void Save()
        {
            Save(FILENAME);
        }

        public void Save(string jsonPath)
        {
            using (FileStream fileStream = File.Open(jsonPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new ConfigDictConverter() }
                };
                JsonSerializer.Serialize(fileStream, this, options);
            }
        }

        /// <summary>Set while deserialising: loading must not write the file back.</summary>
        internal static bool SuppressSave;

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            if (!SuppressSave)
                Translator.Setting?.Save();
        }

        public static bool IsConfigExist()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            return File.Exists(jsonPath);
        }
    }
}