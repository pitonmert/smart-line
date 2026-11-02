import { useApiAvailability, usePlant } from '../context/PlantContext'
import type { DisplayText } from '../i18n/language'
import type { Message } from '../i18n/language'
import { useLanguage } from '../i18n/LanguageContext'
import { useState } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import SimulationPanel from '../components/inspection/SimulationPanel'
import { ErrorNote, Panel, SimulationBanner } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'

export default function SortingPage() {
  const apiAvailable = useApiAvailability()
  const { diagnostics } = usePlant()
  const { date, message, t, text } = useLanguage()
  const queue = usePolling(() => api.getSortQueue(), 3000)
  const [error, setError] = useState<DisplayText | null>(null)
  const [notice, setNotice] = useState<Message | null>(null)

  const release = async (productId: string, code: string) => {
    if (!apiAvailable) return
    if (!confirm(t('Record a logical sort for {code}? No servo command will be sent.', { code }))) return
    setError(null)
    try {
      const result = await api.releaseTrack(productId)
      setNotice({ key: '{code} is {stage}. {message}', params: { code: result.track.publicCode, stage: result.track.stage, message: result.actuation.message } })
      queue.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Sort was not recorded.'))
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {queue.data?.some(item => item.simulation) && <SimulationBanner text={t("One or more queued products are simulated.")} />}
      <Panel title={t("Actuation")}>
        <p className="text-sm">{t("Hardware actuation:")}{' '}{diagnostics.data?.hardwareActuationEnabled ? t('enabled in configuration') : t('disabled by default')}</p>
        <p className="mt-2 text-xs text-text-muted">{t("Logical destinations stay separate from servo motion. The release action records SORTED and does not publish a motion command.")}</p>
      </Panel>
      <Panel title={t("Sorting queue")}>
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="text-text-muted"><tr><th className="py-2">{t("Product")}</th><th>{t("Decision")}</th><th>{t("Destination")}</th><th>{t("Due")}</th><th></th></tr></thead>
            <tbody>
              {(queue.data ?? []).map(item => (
                <tr key={item.productId} className="border-t border-card-border">
                  <td className="py-2">{item.publicCode} {item.simulation && <span className="text-warning-light">{t("SIMULATION")}</span>}</td>
                  <td>{text(item.decision)}</td>
                  <td>{item.destinationBin}</td>
                  <td>{date(item.sortDueAt)}</td>
                  <td>{<button type="button" className="rounded bg-primary px-2 py-1 text-xs text-white" disabled={!apiAvailable} onClick={() => release(item.productId, item.publicCode)}>{t("Release")}</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {queue.data && queue.data.length === 0 && <p className="text-sm text-text-muted">{t("The queue is empty.")}</p>}
        </div>
        {notice && <p className="mt-3 text-sm">{message(notice)}</p>}
        {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
      </Panel>
      <SimulationPanel onChanged={queue.reload} />
    </div>
  )
}
