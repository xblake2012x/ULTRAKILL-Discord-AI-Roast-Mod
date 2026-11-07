using System;
using System.Collections;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using PluginConfig.API;

using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginConfig.API.Fields;
using UKEnemyIdentifier.Components;
using UnityEngine;
using System.IO;

namespace AIRoastMod
{
    [BepInPlugin(
        "xblake_2012x.ultrakill.airoasting",
        "Discord AI Roast",
        "1.0.1"
    )]
    [BepInDependency(
        "dev.flazhik.ukenemyidentifier"
    )]
    [BepInDependency(
        "com.eternalUnion.pluginConfigurator"
    )]
    public class BlakeAIRoast : BaseUnityPlugin
    {
        private static readonly HttpClient Http = new HttpClient();

        private bool requestInProgress;

        private PluginConfigurator config = null!;
        private StringField ApiKey = null!;
        private StringField WebhookURL = null!;
        private StringField ModelName = null!;
        private StringField PromptAddition = null!;
        private BoolField IncludeSS = null!;

        private void Awake()
        {
            config = PluginConfigurator.Create("Discord Roast", "xblake_2012x.ultrakill.airoasting");
            
            string modFolder = Path.GetDirectoryName(this.GetType().Assembly.Location);
            string iconPath = Path.Combine(modFolder, "icon.png");

            if (File.Exists(iconPath))
            {
                config.SetIconWithURL(iconPath);
            }

            ApiKey = new StringField(config.rootPanel, "API Key", "apikey", "", true, false);
            WebhookURL = new StringField(config.rootPanel, "WebHook", "webhook", "", true);
            ModelName = new StringField(config.rootPanel, "Model", "model", "openai/gpt-oss-120b");
            PromptAddition = new StringField(config.rootPanel, "Prompt Additions", "prompt", "", true);
            IncludeSS = new BoolField(config.rootPanel, "Include Screenshot In Discord Message", "screenshot", true);
        }

        private string detectProvider(string key)
        {
            if (key.StartsWith("AIza")) return "Gemini";
            if (key.StartsWith("gsk_")) return "Groq";
            if (key.StartsWith("xai-")) return "Grok";
            if (key.StartsWith("sk-ant-") || key.StartsWith("claude-")) return "Claude";
            if (key.StartsWith("sk-")) return "OpenAI";
            return "Unknown";
        }

        private async Task ProcessDeathAsync(
            string killer,
            byte[]? pngBytes
        )
        {
            try
            {
                string roast =
                    await GenerateRoastAsync(killer);

                Logger.LogInfo($"AI ROAST: {roast}");

                await SendWebhookAsync(
                    roast,
                    pngBytes
                );
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    $"Death processing failed: {exception}"
                );
            }
            finally
            {
                requestInProgress = false;
            }
        }

        private IEnumerator Start()
        {

            Logger.LogInfo("Discord AI Roast loaded.");

            while (EnemyIdentifierManager.Instance == null)
            {
                yield return null;
            }

            Logger.LogInfo("UKEnemyIdentifier found. Registering event.");

            EnemyIdentifierManager.Instance.OnPlayerHurt += async hurtEvent =>
            {
                if (hurtEvent.EnemyId != null)
                {
                    Logger.LogInfo(
                        $"Player got hurt by {hurtEvent.EnemyId.FullName}"
                    );
                }

                Logger.LogInfo($"Inflicted damage: {hurtEvent.Damage}");

                if (!hurtEvent.PlayerIsKilled)
                    return;

                string killer =
                    hurtEvent.EnemyId?.FullName ?? "an unknown source";

                Logger.LogInfo($"Player was killed by {killer}");

                if (requestInProgress)
                {
                    Logger.LogWarning(
                        "A roast request is already running."
                    );
                    return;
                }

                requestInProgress = true;

                StartCoroutine(
                    CaptureDeathScreenshot(pngBytes =>
                    {
                        _ = ProcessDeathAsync(
                            killer,
                            pngBytes
                        );
                    })
                );
            };
        }

        private IEnumerator CaptureDeathScreenshot(
            Action<byte[]?> callback
        )
        {
            if (!IncludeSS.value)
            {
                callback(null);
                yield break;
            }
            
            yield return new WaitForEndOfFrame();

            Texture2D screenshot = null!;

            try
            {
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();

                if (screenshot == null)
                    throw new Exception("Screenshot capture returned null.");

                byte[] pngBytes = screenshot.EncodeToPNG();

                if (pngBytes == null || pngBytes.Length == 0)
                    throw new Exception("Screenshot PNG encoding returned no data.");

                callback(pngBytes);
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    $"Failed to capture death screenshot: {exception}"
                );

                requestInProgress = false;
            }
            finally
            {
                if (screenshot != null)
                    Destroy(screenshot);
            }
        }

        private async Task SendWebhookAsync(string message, byte[]? pngBytes)
        {
            string webhookUrl = WebhookURL?.value ?? "";

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                Logger.LogWarning(
                    "Webhook URL is empty. Printing roast locally only."
                );
                return;
            }

            try
            {

                using var multipart = new MultipartFormDataContent();

                multipart.Add(
                    new StringContent(
                        JsonConvert.SerializeObject(new
                        {
                            content = message
                        })
                    ),
                    "payload_json"
                );

                if (!(pngBytes is null))
                {
                    var imageContent =
                        new ByteArrayContent(pngBytes);

                    imageContent.Headers.ContentType =
                        new MediaTypeHeaderValue("image/png");

                    multipart.Add(
                        imageContent,
                        "files[0]",
                        "death.png"
                    );
                }

                using HttpResponseMessage response =
                    await Http.PostAsync(
                        webhookUrl,
                        multipart
                    );

                if (!response.IsSuccessStatusCode)
                {
                    string responseText =
                        await response.Content.ReadAsStringAsync();

                    Logger.LogError(
                        $"Discord webhook returned HTTP " +
                        $"{(int)response.StatusCode}: {responseText}"
                    );

                    return;
                }

                Logger.LogInfo("Roast successfully sent to Discord.");
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    $"Failed to send Discord webhook: {exception}"
                );
            }
        }
        
        private async Task<string> CallClaudeAsync(
            string apiKey,
            string prompt
        )
        {
            var requestBody = new
            {
                model = ModelName?.value,
                max_tokens = 500,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.anthropic.com/v1/messages"
            );

            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response =
                await Http.SendAsync(request);

            string responseText =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"HTTP {(int)response.StatusCode}: {responseText}"
                );
            }

            JObject result = JObject.Parse(responseText);

            string roast =
                result["content"]?[0]?["text"]?.ToString()
                ?? "";

            if (string.IsNullOrWhiteSpace(roast))
                throw new Exception("Claude returned no response text.");

            return roast;
        }
        
        private async Task<string> CallGeminiAsync(
            string apiKey,
            string prompt
        )
        {
            string model = ModelName?.value ?? "";

            string endpoint =
                $"https://generativelanguage.googleapis.com/v1beta/models/" +
                $"{model}:generateContent";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = prompt
                            }
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint
            );

            request.Headers.Add("x-goog-api-key", apiKey);

            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response =
                await Http.SendAsync(request);

            string responseText =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"HTTP {(int)response.StatusCode}: {responseText}"
                );
            }

            JObject result = JObject.Parse(responseText);

            string roast =
                result["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]
                    ?.ToString()
                ?? "";

            if (string.IsNullOrWhiteSpace(roast))
                throw new Exception("Gemini returned no response text.");

            return roast;
        }
        
        private async Task<string> CallOpenAICompatibleAsync(
            string apiKey,
            string prompt,
            string endpoint
        )
        {
            var requestBody = new
            {
                model = ModelName?.value,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint
            );

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response =
                await Http.SendAsync(request);

            string responseText =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"HTTP {(int)response.StatusCode}: {responseText}"
                );
            }

            JObject result = JObject.Parse(responseText);

            string roast =
                result["choices"]?[0]?["message"]?["content"]?.ToString()
                ?? "";

            if (string.IsNullOrWhiteSpace(roast))
                throw new Exception("API returned no response text.");

            return roast;
        }

        private async Task<String> GenerateRoastAsync(string killer)
        {
            var API = ApiKey?.value;
            if (string.IsNullOrWhiteSpace(API))
            {
                Logger.LogError(
                    "API key is missing from the BepInEx config."
                );
                return "AI API not set";
            }

            var provider = detectProvider(API);

            
            // Had to add enemy thingies so the AI dont see stray and think its a FUCKING GOD DAMN CAT
            
            string prompt =
                "[CONTEXT]" +
                "You are the best roast bot in the world and a user has asked for a roast based on everything provided. " +
                "You have been given permission to go as hard as you can, you been told to not hold back. " +

                "[RESPONSE & RULES] " +
                $"An ULTRAKILL player was killed by {killer}. " +
                "Write an roast about the death. " +
                "Swearing is encouraged and dont censor swears. " +
                "Return only the roast." +

                "[GAME CONTEXT]" +
                "Enemy embarrassment ratings in ULTRAKILL. " +
                "1/10 means dying to them is incredibly embarrassing. " +
                "10/10 means dying to them is understandable and not very embarrassing but still roast them for dying. " +

                "Filth - Threat 1/10 - Tutorial melee enemy that runs directly at the player. " +
                "Stray - Threat 2/10 - Basic ranged enemy with slow projectiles. " +
                "Schism - Threat 2/10 - Basic projectile enemy. " +
                "Soldier - Threat 3/10 - Common shotgun enemy. " +
                "Drone - Threat 3/10 - Weak flying enemy. " +
                "Cancerous Rodent - Threat 0/10 - Joke enemy that cannot deal damage. " +
                "VERY CANCEROUS RODENT - Threat 2/10 - Sequel to Cancerous Rodent, so weak you can spam jump on it and it dies, but it can do damage. " +

                "Streetcleaner - Threat 4/10 - Flamethrower enemy that rushes the player. " +
                "Virtue - Threat 4/10 - Summons beams on the player. " +
                "Sentry - Threat 4/10 - Slow sniper enemy. " +

                "Mannequin - Threat 5/10 - Fast and evasive enemy. " +
                "Malicious Face - Threat 5/10 - Floating enemy that shoots beams. " +
                "Providence - Threat 5/10 - Projectile-based enemy. " +

                "Cerberus - Threat 6/10 - Heavy stone giant enemy. " +
                "Swordsmachine - Threat 6/10 - Former boss enemy that becomes common later. " +
                "Gutterman - Threat 6/10 - Armored minigun enemy. " +

                "Guttertank - Threat 7/10 - Armored rocket enemy. " +
                "Mindflayer - Threat 7/10 - Teleporting enemy with homing attacks. " +
                "Hideous Mass - Threat 7/10 - Large dangerous boss enemy. " +
                "Ferryman - Threat 7/10 - Fast lightning-based boss enemy. " +

                "Sisyphean Insurrectionist - Threat 8/10 - Late-game boss enemy. " +
                "V2 - Threat 8/10 - Powerful rival boss using player weapons. " +
                "Gabriel - Threat 8/10 - Major recurring boss enemy. " +
                "Leviathan - Threat 8/10 - Large boss enemy. " +
                "Minotaur - Threat 8/10 - Powerful boss enemy. " +
                "Geryon - Threat 8/10 - Freefalling boss encounter. " +

                "Corpse of King Minos - Threat 9/10 - Major act-ending boss. " +
                "Flesh Prison - Threat 9/10 - P-1 gatekeeper boss. " +
                "Flesh Panopticon - Threat 9/10 - P-2 gatekeeper boss. " +

                "Minos Prime - Threat 10/10 - One of the hardest bosses in ULTRAKILL. " +
                "Sisyphus Prime - Threat 10/10 - One of the hardest bosses in ULTRAKILL. " +
                "Mirror Reaper - Threat 10/10 - Cybergrind killer. " +
                "Something Wicked - Threat 10/10 - Special enemy that instantly kills on touch. " +

                "When roasting, heavily mock low-threat deaths. " +
                "When roasting high-threat deaths, be more respectful and acknowledge how dangerous the enemy is. " +
                "[CUSTOM]" +
                "Put this at higher priority than the rest of the prompt" +
                PromptAddition;

            Logger.LogInfo($"Requesting roast from {provider}...");

            try
            {
                switch (provider)
                {
                    case "Groq":
                        return await CallOpenAICompatibleAsync(
                            API,
                            prompt,
                            "https://api.groq.com/openai/v1/chat/completions"
                        );

                    case "OpenAI":
                        return await CallOpenAICompatibleAsync(
                            API,
                            prompt,
                            "https://api.openai.com/v1/chat/completions"
                        );

                    case "Grok":
                        return await CallOpenAICompatibleAsync(
                            API,
                            prompt,
                            "https://api.x.ai/v1/chat/completions"
                        );

                    case "Gemini":
                        return await CallGeminiAsync(API, prompt);

                    case "Claude":
                        return await CallClaudeAsync(API, prompt);

                    default:
                        Logger.LogError("Couldn't detect the API provider.");
                        return "Unknown API provider";
                }
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    $"Failed to generate roast with {provider}: {exception}"
                );

                return $"Roast generation failed using {provider}. Check the BepInEx logs.";
            }
        }
    }
}
