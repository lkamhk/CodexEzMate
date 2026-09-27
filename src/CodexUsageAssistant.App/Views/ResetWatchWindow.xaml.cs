using System.Windows;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;

namespace CodexUsageAssistant.Views;

public partial class ResetWatchWindow : Window
{
    public ResetWatchWindow(ResetWatchService watch, AppLanguage? language = null)
    {
        InitializeComponent();
        var model = new ResetWatchViewModel(watch, language);
        DataContext = model;
        Closed += (_, _) => model.Dispose();
    }
}
