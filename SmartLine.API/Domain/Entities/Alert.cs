using SmartLine.API.Domain.Enums;

namespace SmartLine.API.Domain.Entities;

public class Alert
{
    /// <summary>
    /// Alarm kaydının veritabanındaki benzersiz kimliği.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Alarmın türü.
    /// </summary>
    public AlertType AlertType { get; set; }

    /// <summary>
    /// Alarmın halen açık olup olmadığını belirtir.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Alarmın ilk tetiklendiği UTC zaman.
    /// </summary>
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Alarm kapatıldıysa kapatıldığı UTC zaman; aktif alarmlarda boştur.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Alarm kaydının son güncellendiği UTC zaman.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
