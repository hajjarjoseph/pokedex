using System;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class ChatMessage : ViewModelCore {
      public enum MessageRole { User, Assistant, System }

      public MessageRole Role { get; }
      public string Content { get; }
      public string ScriptCode { get; private set; }
      public string Explanation { get; private set; }
      public DateTime Timestamp { get; }

      private string executionResult;
      public string ExecutionResult {
         get => executionResult;
         set => Set(ref executionResult, value);
      }

      private bool isExecuted;
      public bool IsExecuted {
         get => isExecuted;
         set => Set(ref isExecuted, value);
      }

      public bool IsUser => Role == MessageRole.User;
      public bool IsAssistant => Role == MessageRole.Assistant;
      public bool HasScript => !string.IsNullOrEmpty(ScriptCode);

      public ChatMessage(MessageRole role, string content) {
         Role = role;
         Content = content;
         Timestamp = DateTime.Now;
      }

      public void SetScript(string code, string explanation) {
         ScriptCode = code;
         Explanation = explanation;
         NotifyPropertyChanged(nameof(ScriptCode));
         NotifyPropertyChanged(nameof(Explanation));
         NotifyPropertyChanged(nameof(HasScript));
      }
   }
}
