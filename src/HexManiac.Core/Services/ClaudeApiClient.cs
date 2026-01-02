using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HavenSoft.HexManiac.Core.Services {
   public class ClaudeApiClient {
      private const string ApiUrl = "https://api.anthropic.com/v1/messages";
      private const string ApiVersion = "2023-06-01";
      private const string Model = "claude-sonnet-4-20250514";

      private readonly string apiKey;
      private readonly HttpClient httpClient;

      public ClaudeApiClient(string apiKey) {
         this.apiKey = apiKey;
         httpClient = new HttpClient();
         httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
         httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
      }

      public async Task<ClaudeResponse> SendMessageAsync(string systemPrompt, string userMessage, CancellationToken ct) {
         var request = new {
            model = Model,
            max_tokens = 2048,
            system = systemPrompt,
            messages = new[] {
               new { role = "user", content = userMessage }
            }
         };

         var json = JsonSerializer.Serialize(request);
         var content = new StringContent(json, Encoding.UTF8, "application/json");

         var response = await httpClient.PostAsync(ApiUrl, content, ct);
         var responseBody = await response.Content.ReadAsStringAsync(ct);

         if (!response.IsSuccessStatusCode) {
            throw new Exception($"Claude API error: {response.StatusCode} - {responseBody}");
         }

         var result = JsonSerializer.Deserialize<ApiResponse>(responseBody);
         return ParseResponse(result);
      }

      private ClaudeResponse ParseResponse(ApiResponse response) {
         if (response?.Content == null || response.Content.Length == 0) {
            return new ClaudeResponse { Content = "No response from Claude" };
         }

         var textContent = response.Content[0].Text ?? string.Empty;

         // Extract Python code block if present
         var codeMatch = Regex.Match(textContent, @"```python\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
         string scriptCode = null;
         string explanation = textContent;

         if (codeMatch.Success) {
            scriptCode = codeMatch.Groups[1].Value.Trim();
            // Extract explanation (text before the code block)
            var codeStart = textContent.IndexOf("```python", StringComparison.OrdinalIgnoreCase);
            if (codeStart > 0) {
               explanation = textContent.Substring(0, codeStart).Trim();
            }
         }

         return new ClaudeResponse {
            Content = textContent,
            ScriptCode = scriptCode,
            Explanation = explanation
         };
      }

      private class ApiResponse {
         [JsonPropertyName("content")]
         public ContentBlock[] Content { get; set; }

         [JsonPropertyName("stop_reason")]
         public string StopReason { get; set; }
      }

      private class ContentBlock {
         [JsonPropertyName("type")]
         public string Type { get; set; }

         [JsonPropertyName("text")]
         public string Text { get; set; }
      }
   }

   public class ClaudeResponse {
      public string Content { get; set; }
      public string ScriptCode { get; set; }
      public string Explanation { get; set; }
   }
}
