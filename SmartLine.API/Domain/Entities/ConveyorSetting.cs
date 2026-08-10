namespace SmartLine.API.Domain.Entities;

public class ConveyorSetting
{
    /// <summary>
    /// Konveyör ayar kaydının veritabanındaki benzersiz kimliği; sistemde varsayılan kayıt 1 kullanılır.
    /// </summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// Konveyörün hedef çalışma hızı; API ve firmware komutlarında yüzde değer olarak kullanılır.
    /// </summary>
    public int Speed { get; set; } = 50;

    /// <summary>
    /// Konveyörün çalışır durumda olup olmadığını belirtir.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Persists the software emergency latch. Only an authorized reset may clear it.
    /// </summary>
    public bool IsEmergencyStopped { get; set; }

    /// <summary>
    /// Stores the latest fire sensor state independently from the emergency latch.
    /// </summary>
    public bool IsFireDetected { get; set; }

    /// <summary>
    /// Monotonically increases for every authorized emergency reset. Firmware persists the last
    /// applied generation so an old retained command can never clear a newer local safety latch.
    /// </summary>
    public int EmergencyResetGeneration { get; set; }

    /// <summary>
    /// Identifies the current firmware fire-safety epoch. A new epoch is treated as a
    /// commissioning event and requires an explicit administrator reset. Device epochs are
    /// represented on the wire as exactly 32 lowercase hexadecimal characters.
    /// </summary>
    public string FireDeviceEpoch { get; set; } = string.Empty;

    /// <summary>
    /// Highest persisted local-fire generation reported by firmware and acknowledged by the API.
    /// Emergency reset commands echo this value so stale retained resets cannot clear a newer latch.
    /// </summary>
    public long LastFireGeneration { get; set; }

    /// <summary>
    /// Fail-safe state set when a device reports a lower generation within the same epoch.
    /// The fault cannot be reset; the device must establish a new epoch.
    /// </summary>
    public bool IsFireGenerationFault { get; set; }

    /// <summary>
    /// Sensörlerden gelen son ortam sıcaklığı değeri.
    /// </summary>
    public decimal Temperature { get; set; }

    /// <summary>
    /// Sensörlerden gelen son ortam nemi değeri.
    /// </summary>
    public decimal Humidity { get; set; }

    /// <summary>
    /// Fanın son bilinen aktif/pasif durumunu belirtir.
    /// </summary>
    public bool FanActive { get; set; } = false;

    /// <summary>
    /// Konveyör ayarlarının veya ortam değerlerinin son güncellendiği UTC zaman.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
