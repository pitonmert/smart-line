using SmartLine.API.Domain.Enums;

namespace SmartLine.API.Domain.Entities;

public class SystemLog
{
    /// <summary>
    /// Log kaydının veritabanındaki benzersiz kimliği.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Loglanan olayın türü; sistem, ürün, alarm veya konveyör olaylarını ayırt eder.
    /// </summary>
    public EventType EventType { get; set; }

    /// <summary>
    /// Olayla ilgili okunabilir açıklama veya detay mesajı.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Log önem seviyesi; Info, Warning, Error veya Critical gibi değerler taşır.
    /// </summary>
    public string Severity { get; set; } = "Info";

    /// <summary>
    /// Log kaydının oluşturulduğu UTC zaman.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
