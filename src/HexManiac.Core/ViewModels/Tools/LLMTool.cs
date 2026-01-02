using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class LLMTool : ViewModelCore {
      private readonly EditorViewModel editor;
      private readonly PythonTool pythonTool;
      private CancellationTokenSource cts;

      public ObservableCollection<ChatMessage> Messages { get; } = new();

      private string inputText = string.Empty;
      public string InputText {
         get => inputText;
         set => Set(ref inputText, value);
      }

      private string apiKey = string.Empty;
      public string ApiKey {
         get => apiKey;
         set => Set(ref apiKey, value);
      }

      private bool isProcessing;
      public bool IsProcessing {
         get => isProcessing;
         set {
            if (TryUpdate(ref isProcessing, value)) {
               sendCommand.RaiseCanExecuteChanged();
               cancelCommand.RaiseCanExecuteChanged();
            }
         }
      }

      private string errorMessage;
      public string ErrorMessage {
         get => errorMessage;
         set {
            if (TryUpdate(ref errorMessage, value)) {
               NotifyPropertyChanged(nameof(HasError));
            }
         }
      }

      public bool HasError => !string.IsNullOrEmpty(errorMessage);

      private readonly StubCommand sendCommand;
      private readonly StubCommand cancelCommand;
      private readonly StubCommand executeScriptCommand;
      private readonly StubCommand clearCommand;

      public ICommand SendCommand => sendCommand;
      public ICommand CancelCommand => cancelCommand;
      public ICommand ExecuteScriptCommand => executeScriptCommand;
      public ICommand ClearCommand => clearCommand;

      public LLMTool(EditorViewModel editor, PythonTool pythonTool) {
         this.editor = editor;
         this.pythonTool = pythonTool;

         sendCommand = new StubCommand {
            CanExecute = arg => !IsProcessing && !string.IsNullOrWhiteSpace(InputText),
            Execute = arg => _ = SendMessageAsync()
         };
         cancelCommand = new StubCommand {
            CanExecute = arg => IsProcessing,
            Execute = arg => Cancel()
         };
         executeScriptCommand = new StubCommand {
            CanExecute = arg => arg is ChatMessage msg && msg.HasScript && !msg.IsExecuted,
            Execute = arg => ExecuteScript((ChatMessage)arg)
         };
         clearCommand = new StubCommand {
            CanExecute = arg => Messages.Count > 0,
            Execute = arg => Messages.Clear()
         };

         // Add welcome message
         Messages.Add(new ChatMessage(ChatMessage.MessageRole.Assistant,
            "Hello! I can help you edit your Pokemon ROM. Try asking me to:\n" +
            "- Change a Pokemon's stats\n" +
            "- Modify trainer teams\n" +
            "- Edit items or moves\n\n" +
            "Make sure to set your Claude API key first."));
      }

      public async Task SendMessageAsync() {
         if (string.IsNullOrWhiteSpace(InputText)) return;
         if (string.IsNullOrWhiteSpace(ApiKey)) {
            ErrorMessage = "Please set your Claude API key";
            return;
         }

         var userMessage = InputText.Trim();
         InputText = string.Empty;
         Messages.Add(new ChatMessage(ChatMessage.MessageRole.User, userMessage));

         IsProcessing = true;
         ErrorMessage = null;
         cts = new CancellationTokenSource();

         try {
            var response = await CallClaudeApiAsync(userMessage, cts.Token);
            if (response != null) {
               Messages.Add(response);
               // Auto-execute if we got a script
               if (response.HasScript) {
                  ExecuteScript(response);
               }
            }
         } catch (OperationCanceledException) {
            Messages.Add(new ChatMessage(ChatMessage.MessageRole.Assistant, "(Cancelled)"));
         } catch (Exception ex) {
            ErrorMessage = ex.Message;
            Messages.Add(new ChatMessage(ChatMessage.MessageRole.Assistant, $"Error: {ex.Message}"));
         } finally {
            IsProcessing = false;
            cts?.Dispose();
            cts = null;
         }
      }

      private void Cancel() {
         cts?.Cancel();
      }

      public void ExecuteScript(ChatMessage message) {
         if (message == null || !message.HasScript || message.IsExecuted) return;

         var result = pythonTool.RunPythonScript(message.ScriptCode);
         message.ExecutionResult = result.HasError && !result.IsWarning
            ? $"Error: {result.ErrorMessage}"
            : result.ErrorMessage ?? "Done";
         message.IsExecuted = true;

         editor.SelectedTab?.Refresh();
      }

      private async Task<ChatMessage> CallClaudeApiAsync(string userMessage, CancellationToken ct) {
         // Build context
         var context = BuildContext();
         var schema = BuildSchema();

         var systemPrompt = $@"You are an assistant for HexManiacAdvance, a Pokemon GBA ROM editor.
{context}

Available Tables and Fields:
{schema}

Python API:
- data.{{table}}[i].{{field}} - read/write field value
- data.{{table}}[i].{{field}} = value - set value (int, string, or enum name)
- print(text) - show message to user
- for item in data.{{table}}: ... - iterate table

When the user asks to modify data, respond with Python code wrapped in ```python blocks.
Always explain what the code does briefly before the code block.
Keep code simple and focused on the specific request.";

         // Call Claude API
         var client = new ClaudeApiClient(ApiKey);
         var response = await client.SendMessageAsync(systemPrompt, userMessage, ct);

         var assistantMessage = new ChatMessage(ChatMessage.MessageRole.Assistant, response.Content);
         if (!string.IsNullOrEmpty(response.ScriptCode)) {
            assistantMessage.SetScript(response.ScriptCode, response.Explanation);
         }
         return assistantMessage;
      }

      private string BuildContext() {
         if (editor.SelectedTab is not IViewPort viewPort) {
            return "No ROM loaded.";
         }

         var model = viewPort.Model;
         var gameCode = model.GetGameCode();
         return $"ROM: {gameCode}";
      }

      private string BuildSchema() {
         if (editor.SelectedTab is not IViewPort viewPort) {
            return "No tables available.";
         }

         // MVP: hardcoded common tables
         return @"data.pokemon.stats: hp, attack, defense, speed, spAttack, spDefense, type1, type2, catchRate, baseExp, evYield, item1, item2, abilities
data.pokemon.names: name (text)
data.pokemon.moves.levelup: [pokemon][move, level]
data.trainers.stats: pokemon (team pointer), ai, class, items
data.trainers.pokemon: ivSpread, level, pokemon, item, moves
data.items.stats: name, price, holdEffect, parameter, pocket
data.pokemon.moves.names: name (text)
data.pokemon.moves.stats: power, type, accuracy, pp, effect";
      }

      public void Close() => editor.ShowLLMPanel = false;
   }
}
