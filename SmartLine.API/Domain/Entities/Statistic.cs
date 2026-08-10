namespace SmartLine.API.Domain.Entities;

public class Statistic
{
    /// <summary>
    /// İstatistik kaydının veritabanındaki benzersiz kimliği.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// İstatistiğin yapılandırılmış tesis saat dilimindeki tarihi.
    /// </summary>
    public DateOnly Date { get; set; }

    /// <summary>
    /// Saatlik istatistikler için tesisin yerel saat değeri; günlük toplam kayıtlarda boştur.
    /// </summary>
    public int? Hour { get; set; }

    /// <summary>
    /// İlgili tarih/saat aralığında işlenen toplam ürün sayısı.
    /// </summary>
    public int TotalProducts { get; set; } = 0;

    /// <summary>
    /// İlgili tarih/saat aralığında hatalı olarak işaretlenen ürün sayısı.
    /// </summary>
    public int DefectiveProducts { get; set; } = 0;

    /// <summary>
    /// İlgili tarih/saat aralığında kırmızı olarak sınıflandırılan ürün sayısı.
    /// </summary>
    public int RedProducts { get; set; } = 0;

    /// <summary>
    /// İlgili tarih/saat aralığında mavi olarak sınıflandırılan ürün sayısı.
    /// </summary>
    public int BlueProducts { get; set; } = 0;

    /// <summary>
    /// İlgili tarih/saat aralığında yeşil olarak sınıflandırılan ürün sayısı.
    /// </summary>
    public int GreenProducts { get; set; } = 0;

    /// <summary>
    /// İlgili tarih/saat aralığında sarı olarak sınıflandırılan ürün sayısı.
    /// </summary>
    public int YellowProducts { get; set; } = 0;
}
