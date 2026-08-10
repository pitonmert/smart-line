import { useEffect, useState } from 'react'
import { useDashboardState } from '../../context/DashboardContext'
import { api } from '../../api/client'
import type { StatisticsDto } from '../../types'
import ChartModeToggle from './ChartModeToggle'

type Tab = 'production' | 'products'

const colorMap: Record<string, string> = {
  RED: 'bg-red-500',
  BLUE: 'bg-blue-500',
  GREEN: 'bg-green-500',
  YELLOW: 'bg-yellow-500',
}

const statusMap: Record<string, { label: string; cls: string }> = {
  OK: { label: 'Başarılı', cls: 'text-success-light' },
  DEFECTIVE: { label: 'Hatalı', cls: 'text-danger-light' },
  NO_BARCODE: { label: 'Barkod Yok', cls: 'text-warning-light' },
  WRONG_COLOR: { label: 'Yanlış Renk', cls: 'text-warning-light' },
}

export default function ProductionAndProducts() {
  const [tab, setTab] = useState<Tab>('production')
  const { chartMode, liveProductCounts, recentProducts } = useDashboardState()
  const [hourlyData, setHourlyData] = useState<StatisticsDto[]>([])

  useEffect(() => {
    if (tab === 'production' && chartMode === 'historical') {
      api.getHourlyStats().then(setHourlyData).catch(() => {})
    }
  }, [tab, chartMode])

  const productionLabel = chartMode === 'live' ? 'Canlı Üretim' : 'Saatlik Üretim'

  return (
    <div className="bg-card rounded-xl border border-card-border p-5">
      <div className="flex items-center justify-between mb-4">
        <div className="flex gap-1">
          <button
            onClick={() => setTab('production')}
            className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
              tab === 'production'
                ? 'bg-primary/20 text-primary-light'
                : 'text-text-muted hover:text-text hover:bg-card-border/30'
            }`}
          >
            {productionLabel}
          </button>
          <button
            onClick={() => setTab('products')}
            className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
              tab === 'products'
                ? 'bg-primary/20 text-primary-light'
                : 'text-text-muted hover:text-text hover:bg-card-border/30'
            }`}
          >
            Son Ürünler
          </button>
        </div>
        {tab === 'production' && <ChartModeToggle />}
      </div>

      <div className="overflow-x-auto">
        {tab === 'production' ? (
          chartMode === 'live' ? (
            liveProductCounts.length === 0 ? (
              <p className="text-sm text-text-muted">Henüz veri yok — ürün geçişi bekleniyor</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-text-muted text-xs border-b border-card-border">
                    <th className="text-left pb-2 font-medium">Zaman</th>
                    <th className="text-right pb-2 font-medium text-success-light">Başarılı</th>
                    <th className="text-right pb-2 font-medium text-danger-light">Hatalı</th>
                    <th className="text-right pb-2 font-medium">Toplam</th>
                  </tr>
                </thead>
                <tbody>
                  {liveProductCounts.map(c => (
                    <tr key={c.time} className="border-b border-card-border/50 last:border-0">
                      <td className="py-1.5 font-mono text-xs">{c.time}</td>
                      <td className="py-1.5 text-right text-xs text-success-light">{c.ok}</td>
                      <td className="py-1.5 text-right text-xs text-danger-light">{c.defective}</td>
                      <td className="py-1.5 text-right text-xs">{c.ok + c.defective}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
          ) : (
            hourlyData.length === 0 ? (
              <p className="text-sm text-text-muted">Saatlik veri bulunamadı</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-text-muted text-xs border-b border-card-border">
                    <th className="text-left pb-2 font-medium">Saat</th>
                    <th className="text-right pb-2 font-medium text-success-light">Başarılı</th>
                    <th className="text-right pb-2 font-medium text-danger-light">Hatalı</th>
                    <th className="text-right pb-2 font-medium">Toplam</th>
                    <th className="text-right pb-2 font-medium">Hata %</th>
                  </tr>
                </thead>
                <tbody>
                  {hourlyData.map(s => (
                    <tr key={s.id} className="border-b border-card-border/50 last:border-0">
                      <td className="py-1.5 font-mono text-xs">{s.hour ?? 0}:00</td>
                      <td className="py-1.5 text-right text-xs text-success-light">{s.totalProducts - s.defectiveProducts}</td>
                      <td className="py-1.5 text-right text-xs text-danger-light">{s.defectiveProducts}</td>
                      <td className="py-1.5 text-right text-xs">{s.totalProducts}</td>
                      <td className="py-1.5 text-right text-xs text-text-muted">{s.defectiveRate.toFixed(1)}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
          )
        ) : (
          recentProducts.length === 0 ? (
            <p className="text-sm text-text-muted">Henüz ürün kaydı yok</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-text-muted text-xs border-b border-card-border">
                  <th className="text-left pb-2 font-medium">Barkod</th>
                  <th className="text-left pb-2 font-medium">Renk</th>
                  <th className="text-left pb-2 font-medium">Durum</th>
                  <th className="text-right pb-2 font-medium">Zaman</th>
                </tr>
              </thead>
              <tbody>
                {recentProducts.map(p => {
                  const status = statusMap[p.status] ?? { label: p.status, cls: 'text-text-muted' }
                  return (
                    <tr key={p.id} className="border-b border-card-border/50 last:border-0">
                      <td className="py-2 font-mono text-xs">{p.barcode}</td>
                      <td className="py-2">
                        {p.color ? (
                          <span className="inline-flex items-center gap-1.5">
                            <span className={`w-2.5 h-2.5 rounded-full ${colorMap[p.color] ?? 'bg-gray-500'}`} />
                            <span className="text-xs">{p.color}</span>
                          </span>
                        ) : (
                          <span className="text-xs text-text-muted">—</span>
                        )}
                      </td>
                      <td className={`py-2 text-xs font-medium ${status.cls}`}>{status.label}</td>
                      <td className="py-2 text-right text-xs text-text-muted">
                        {new Date(p.passedAt).toLocaleTimeString('tr-TR')}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )
        )}
      </div>
    </div>
  )
}
