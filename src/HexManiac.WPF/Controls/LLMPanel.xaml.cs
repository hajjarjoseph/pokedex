using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class LLMPanel {
      public LLMPanel() {
         InitializeComponent();
         DataContextChanged += (s, e) => {
            if (e.OldValue is LLMTool oldTool) {
               oldTool.PropertyChanged -= OnToolPropertyChanged;
            }
            if (DataContext is LLMTool tool) {
               ApiKeyBox.Password = tool.ApiKey;
               tool.Messages.CollectionChanged += (_, _) => ScrollToBottom();
               tool.PropertyChanged += OnToolPropertyChanged;
            }
         };
      }

      private void OnToolPropertyChanged(object sender, PropertyChangedEventArgs e) {
         if (e.PropertyName == nameof(LLMTool.IsProcessing)) {
            ScrollToBottom();
         }
      }

      private void InputKeyDown(object sender, KeyEventArgs e) {
         if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) {
            e.Handled = true;
            if (DataContext is LLMTool tool && tool.SendCommand.CanExecute(null)) {
               tool.SendCommand.Execute(null);
            }
         } else if (e.Key == Key.Escape) {
            e.Handled = true;
            if (DataContext is LLMTool tool) {
               tool.Close();
            }
         }
      }

      private void ApiKeyChanged(object sender, System.Windows.RoutedEventArgs e) {
         if (DataContext is LLMTool tool) {
            tool.ApiKey = ApiKeyBox.Password;
         }
      }

      private void ScrollToBottom() {
         Dispatcher.BeginInvoke(() => {
            MessagesScroller.ScrollToEnd();
         });
      }
   }
}
