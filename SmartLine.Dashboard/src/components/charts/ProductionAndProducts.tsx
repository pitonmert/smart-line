import { useLanguage } from '../../i18n/LanguageContext'
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
  OK: { label: 'Accepted', cls: 'text-success-light' },
  DEFECTIVE: { label: 'Rejected', cls: 'text-danger-light' },
  NO_BARCODE: { label: 'No barcode', cls: 'text-warning-light' },
  WRONG_COLOR: { label: 'Wrong color', cls: 'text-warning-light' },
}

export default function ProductionAndProducts() {
  const { number, t, text, time } = useLanguage()
  const [tab, setTab] = useState<Tab>('production')
  const { chartMode, liveProductCounts, recentProducts } = useDashboardState()
  const [hourlyData, setHourlyData] = useState<StatisticsDto[]>([])

  useEffect(() => {
    if (tab === 'production' && chartMode === 'historical') {
      api.getHourlyStats().then(setHourlyData).catch(() => {})
    }
  }, [tab, chartMode])

  const productionLabel = chartMode === 'live' ? 'Live production' : 'Hourly production'

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
            {text(productionLabel)}
          </button>
          <button
            onClick={() => setTab('products')}
            className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
              tab === 'products'
                ? 'bg-primary/20 text-primary-light'
                : 'text-text-muted hover:text-text hover:bg-card-border/30'
            }`}
          >{t("Recent products")}</button>
        </div>
        {tab === 'production' && <ChartModeToggle />}
      </div>

      <div className="overflow-x-auto">
        {tab === 'production' ? (
          chartMode === 'live' ? (
            liveProductCounts.length === 0 ? (
              <p className="text-sm text-text-muted">{t("No data yet — waiting for products")}</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-text-muted text-xs border-b border-card-border">
                    <th className="text-left pb-2 font-medium">{t("Time")}</th>
                    <th className="text-right pb-2 font-medium text-success-light">{t("Accepted")}</th>
                    <th className="text-right pb-2 font-medium text-danger-light">{t("Rejected")}</th>
                    <th className="text-right pb-2 font-medium">{t("Total")}</th>
                  </tr>
                </thead>
                <tbody>
                  {liveProductCounts.map(c => (
                    <tr key={c.time} className="border-b border-card-border/50 last:border-0">
                      <td className="py-1.5 font-mono text-xs">{c.time}</td>
                      <td className="py-1.5 text-right text-xs text-success-light">{number(c.ok)}</td>
                      <td className="py-1.5 text-right text-xs text-danger-light">{number(c.defective)}</td>
                      <td className="py-1.5 text-right text-xs">{number(c.ok + c.defective)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
          ) : (
            hourlyData.length === 0 ? (
              <p className="text-sm text-text-muted">{t("No hourly data")}</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-text-muted text-xs border-b border-card-border">
                    <th className="text-left pb-2 font-medium">{t("Hour")}</th>
                    <th className="text-right pb-2 font-medium text-success-light">{t("Accepted")}</th>
                    <th className="text-right pb-2 font-medium text-danger-light">{t("Rejected")}</th>
                    <th className="text-right pb-2 font-medium">{t("Total")}</th>
                    <th className="text-right pb-2 font-medium">{t("Reject %")}</th>
                  </tr>
                </thead>
                <tbody>
                  {hourlyData.map(s => (
                    <tr key={s.id} className="border-b border-card-border/50 last:border-0">
                      <td className="py-1.5 font-mono text-xs">{s.hour ?? 0}:00</td>
                      <td className="py-1.5 text-right text-xs text-success-light">{number(s.totalProducts - s.defectiveProducts)}</td>
                      <td className="py-1.5 text-right text-xs text-danger-light">{number(s.defectiveProducts)}</td>
                      <td className="py-1.5 text-right text-xs">{number(s.totalProducts)}</td>
                      <td className="py-1.5 text-right text-xs text-text-muted">{number(s.defectiveRate, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
          )
        ) : (
          recentProducts.length === 0 ? (
            <p className="text-sm text-text-muted">{t("No products yet")}</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-text-muted text-xs border-b border-card-border">
                  <th className="text-left pb-2 font-medium">{t("Barcode")}</th>
                  <th className="text-left pb-2 font-medium">{t("Color")}</th>
                  <th className="text-left pb-2 font-medium">{t("Status")}</th>
                  <th className="text-right pb-2 font-medium">{t("Time")}</th>
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
                            <span className="text-xs">{text(p.color)}</span>
                          </span>
                        ) : (
                          <span className="text-xs text-text-muted">—</span>
                        )}
                      </td>
                      <td className={`py-2 text-xs font-medium ${status.cls}`}>{text(status.label)}</td>
                      <td className="py-2 text-right text-xs text-text-muted">
                        {time(p.passedAt)}
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
