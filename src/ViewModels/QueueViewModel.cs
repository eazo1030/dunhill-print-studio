using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Services;
using System.Collections.ObjectModel;

namespace Dunhill.PrintStudio.ViewModels;

public partial class QueueViewModel : ObservableObject
{
    public ObservableCollection<PrintJob> Pending { get; } = new();
    public ObservableCollection<PrintJob> Completed { get; } = new();

    [ObservableProperty] private string? lastError;

    [RelayCommand]
    private async Task RetryAsync(PrintJob? job)
    {
        if (job == null) return;
        // Re-submit the job to the printer
        // (Phase 2: hook into cloud sync + retry with backoff)
        await Task.CompletedTask;
    }
}

public partial class HistoryViewModel : ObservableObject
{
    public ObservableCollection<PrintJob> History { get; } = new();
}

public partial class InventoryViewModel : ObservableObject
{
    public ObservableCollection<InventoryItem> Items { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        // Phase 3: fetch from cloud
        // For now, populate with sample data so the UI isn't empty
        Items.Clear();
        Items.Add(new InventoryItem("WIDGET-A", "Standard Widget", "Hardware", 124, 25, "A-12", null, DateTime.UtcNow));
        Items.Add(new InventoryItem("BOLT-M8", "M8 Bolt 25mm", "Hardware", 1840, 500, "B-03", null, DateTime.UtcNow));
        Items.Add(new InventoryItem("CABLE-USB-C", "USB-C Cable 1m", "Cables", 38, 50, "C-08", null, DateTime.UtcNow));
    }
}
