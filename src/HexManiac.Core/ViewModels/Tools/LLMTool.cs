using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Services;
using HavenSoft.HexManiac.Core.ViewModels.Map;
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
            "With Map Editor open, I can also:\n" +
            "- Move or modify NPCs\n" +
            "- Edit wild Pokemon encounters\n" +
            "- Modify warps and signposts\n\n" +
            "Set your Claude API key to get started."));
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

      private IViewPort FindViewPort() {
         // First check selected tab
         if (editor.SelectedTab is IViewPort vp) return vp;

         // If selected tab is not a ViewPort (e.g., MapEditor), find one from all tabs
         foreach (var tab in editor) {
            if (tab is IViewPort viewPort) return viewPort;
         }
         return null;
      }

      private string BuildContext() {
         var viewPort = FindViewPort();
         if (viewPort == null) {
            return "No ROM loaded.";
         }

         var model = viewPort.Model;
         var gameCode = model.GetGameCode();
         var context = $"ROM: {gameCode}";

         // Check if Map Editor is open and include map contents
         if (editor.SelectedTab is MapEditorViewModel mapEditor && mapEditor.PrimaryMap != null) {
            var map = mapEditor.PrimaryMap;
            var bankNum = map.MapID / 1000;
            var mapNum = map.MapID % 1000;
            context += $"\n\n=== MAP EDITOR OPEN ===";
            context += $"\nCurrent map: Bank {bankNum}, Map {mapNum}";
            context += $"\nAccess via: data.maps.banks[{bankNum}].maps[{mapNum}].map.events";

            // List all events on the map
            context += BuildMapContents(mapEditor);
         }

         return context;
      }

      private string BuildMapContents(MapEditorViewModel mapEditor) {
         var sb = new System.Text.StringBuilder();
         var map = mapEditor.PrimaryMap;

         // Get events from the map
         try {
            // Object Events (NPCs)
            var objects = map.EventGroup?.Objects;
            if (objects != null && objects.Count > 0) {
               sb.Append($"\n\nObject Events (NPCs) - {objects.Count} total:");
               for (int i = 0; i < Math.Min(objects.Count, 15); i++) {
                  var obj = objects[i];
                  sb.Append($"\n  [{i}] pos=({obj.X},{obj.Y}) graphics={obj.Graphics} moveType={obj.MoveType}");
                  if (obj.TrainerType > 0) sb.Append($" trainer");
               }
               if (objects.Count > 15) sb.Append($"\n  ... and {objects.Count - 15} more");
            }

            // Warps
            var warps = map.EventGroup?.Warps;
            if (warps != null && warps.Count > 0) {
               sb.Append($"\n\nWarps - {warps.Count} total:");
               for (int i = 0; i < Math.Min(warps.Count, 10); i++) {
                  var warp = warps[i];
                  sb.Append($"\n  [{i}] pos=({warp.X},{warp.Y}) -> bank={warp.Bank} map={warp.Map} warpId={warp.WarpID}");
               }
            }

            // Signposts
            var signposts = map.EventGroup?.Signposts;
            if (signposts != null && signposts.Count > 0) {
               sb.Append($"\n\nSignposts - {signposts.Count} total:");
               for (int i = 0; i < Math.Min(signposts.Count, 10); i++) {
                  var sign = signposts[i];
                  sb.Append($"\n  [{i}] pos=({sign.X},{sign.Y}) kind={sign.Kind}");
               }
            }

            // Script triggers
            var scripts = map.EventGroup?.Scripts;
            if (scripts != null && scripts.Count > 0) {
               sb.Append($"\n\nScript Triggers - {scripts.Count} total:");
               for (int i = 0; i < Math.Min(scripts.Count, 10); i++) {
                  var script = scripts[i];
                  sb.Append($"\n  [{i}] pos=({script.X},{script.Y})");
               }
            }

         } catch {
            sb.Append("\n(Could not read map events)");
         }

         return sb.ToString();
      }

      private string BuildSchema() {
         var viewPort = FindViewPort();
         if (viewPort == null) {
            return "No tables available.";
         }

         // Base Pokemon/trainer tables
         var schema = @"=== Pokemon & Trainer Data ===
data.pokemon.stats: hp, attack, defense, speed, spAttack, spDefense, type1, type2, catchRate, baseExp, evYield, item1, item2, abilities
data.pokemon.names: name (text)
data.pokemon.moves.levelup: [pokemon][move, level]
data.trainers.stats: pokemon (team pointer), ai, class, items
data.trainers.pokemon: ivSpread, level, pokemon, item, moves
data.items.stats: name, price, holdEffect, parameter, pocket
data.pokemon.moves.names: name (text)
data.pokemon.moves.stats: power, type, accuracy, pp, effect";

         // Add map schema if Map Editor is open
         if (editor.SelectedTab is MapEditorViewModel) {
            schema += @"

=== Map Data (Map Editor is open) ===
Access pattern: data.maps.banks[bankNum].maps[mapNum].map.events

Object Events (NPCs):
  .events.objects[i]: id, graphics, x, y, elevation, moveType, range, trainerType, trainerRangeOrBerryID, script, flag
  - graphics: sprite ID from graphics.overworld.sprites
  - x, y: position on map (0-based)
  - moveType: 0=none, 1=look_around, 2=walk_around, etc.
  - script: pointer to XSE script (what happens on interaction)
  - flag: event flag (NPC hidden when flag is set)

Warps:
  .events.warps[i]: x, y, elevation, warpID, map, bank
  - warpID: destination warp point ID
  - map, bank: destination map coordinates

Script Triggers:
  .events.scripts[i]: x, y, elevation, trigger, index, script

Signposts:
  .events.signposts[i]: x, y, elevation, kind, arg
  - kind: 0-4=script signpost, 5-7=hidden item, 8=secret base
  - arg: script pointer or item ID depending on kind

Wild Pokemon:
  data.pokemon.wild[mapId].grass.list[i]: low (min level), high (max level), species
  data.pokemon.wild[mapId].surf.list[i]: same structure
  data.pokemon.wild[mapId].fish.list[i]: same structure (0-1=old rod, 2-4=good rod, 5+=super rod)

Example - Add NPC that gives item:
  events = data.maps.banks[3].maps[0].map.events
  npc = events.objects[0]  # modify existing or find empty slot
  npc.x = 10
  npc.y = 8
  npc.graphics = 5  # sprite ID
  npc.moveType = 1  # look around";
         }

         return schema;
      }

      public void Close() => editor.ShowLLMPanel = false;
   }
}
