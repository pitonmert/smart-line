import { usePlant } from '../context/PlantContext'
import { useLanguage } from '../i18n/LanguageContext'
import ProductionAndProducts from '../components/charts/ProductionAndProducts'
import ProductionCounters from '../components/inspection/ProductionCounters'
import { ErrorNote, Panel } from '../components/inspection/ui'

export default function AnalyticsPage() {
  const { room } = usePlant()
  const { t } = useLanguage()
  return (
    <div className="flex flex-col gap-4">
      {room.error && <ErrorNote>{room.error}</ErrorNote>}
      <p className="text-xs text-text-muted">{t("Real production only. Simulation is excluded.")}</p>
      <ProductionCounters />
      <Panel title={t("Inspection destinations")}>
        <ul className="grid grid-cols-2 gap-2 text-sm md:grid-cols-4">
          {(room.data?.binCounts ?? []).map(bin => <li key={bin.destinationBin} className="rounded-lg border border-card-border px-3 py-2">{bin.destinationBin}<div className="font-mono text-lg">{bin.count}</div></li>)}
        </ul>
        <p className="mt-3 text-xs text-text-muted">{t("Yield is pass divided by pass plus reject. Manual review is outside that denominator. An empty decided population is shown as a dash.")}</p>
      </Panel>
      <Panel title={t("Barcode production counter")}>
        <ProductionAndProducts />
        <p className="mt-2 text-xs text-text-muted">{t("This chart is the existing barcode counter. It is separate from recipe inspection yield.")}</p>
      </Panel>
    </div>
  )
}
