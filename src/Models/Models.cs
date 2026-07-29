namespace Dunhill.PrintStudio.Models;

public sealed record InventoryItem(
    string Sku,
    string Name,
    string Category,
    int QuantityOnHand,
    int ReorderAt,
    string? Location,
    string? Epc,
    DateTime UpdatedAt
);

public sealed record PrintJob(
    string JobId,
    string Sku,
    string Name,
    int Qty,
    string? Serial,
    string? Epc,
    bool EncodeRfid,
    PrintJobStatus Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? Error
)
{
    public string StatusLabel => Status switch
    {
        PrintJobStatus.Pending => "Pending",
        PrintJobStatus.Printing => "Printing…",
        PrintJobStatus.Done => "Printed",
        PrintJobStatus.Failed => "Failed",
        PrintJobStatus.VoidLabel => "Void label (RFID failed)",
        PrintJobStatus.Cancelled => "Cancelled",
        _ => Status.ToString()
    };
}

public enum PrintJobStatus
{
    Pending,
    Printing,
    Done,
    Failed,
    VoidLabel,
    Cancelled
}

public sealed record PrinterStatus(
    bool Online,
    string? Model,
    string? ConnectionType,    // "USB" or "TCP 192.168.1.50:9100"
    string? LastError,
    DateTime LastChecked
);
