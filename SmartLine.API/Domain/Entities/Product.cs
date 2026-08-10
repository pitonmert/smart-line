using SmartLine.API.Domain.Enums;

namespace SmartLine.API.Domain.Entities;

public class Product
{
    /// <summary>
    /// Ürünün veritabanındaki benzersiz kimliği.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Ürün üzerinde okunan barkod değeri; mükerrer ürünleri ayırt etmek için kullanılır.
    /// </summary>
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    /// Renk sensöründen veya görüntü hattından gelen ürün rengi; okunamadığında boş kalabilir.
    /// </summary>
    public ProductColor? Color { get; set; }

    /// <summary>
    /// Ürünün kalite/işleme durumu.
    /// </summary>
    public ProductStatus Status { get; set; } = ProductStatus.OK;

    /// <summary>
    /// Ürünün konveyör hattından geçtiği UTC zaman.
    /// </summary>
    public DateTime PassedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Ürün kaydının son güncellendiği UTC zaman; audit ve eşzamanlılık takibi için kullanılır.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

}
