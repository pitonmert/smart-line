import { useLanguage } from '../i18n/LanguageContext'
import { Fragment, useState, type FormEvent } from 'react'
import AnalyticsPage from './AnalyticsPage'
import InspectionDetails from '../components/inspection/InspectionDetails'
import { api } from '../api/client'
import { ErrorNote, Panel } from '../components/inspection/ui'
import { usePolling } from '../hooks/usePolling'

export default function HistoryPage() {
  const { date, number, t, text } = useLanguage()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [summaryOpen, setSummaryOpen] = useState(false)
  const history = usePolling(
    () => api.getInspections(`${query ? `${query}&` : '?'}pageNumber=${page}&pageSize=20`),
    5000,
    [query, page],
  )

  const search = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const params = new URLSearchParams()
    for (const key of ['decision', 'productClass', 'q']) {
      const value = String(form.get(key) || '').trim()
      if (value) params.set(key, value)
    }
    if (form.get('simulation') === 'true' || form.get('simulation') === 'false') params.set('simulation', String(form.get('simulation')))
    const text = params.toString()
    setPage(1)
    setQuery(text ? `?${text}` : '')
  }

  return <div className="history-page">
    <div className="page-heading"><div><p className="eyebrow">SmartLine</p><h1>{t('History')}</h1></div>
      <button type="button" className="utility-button" aria-expanded={summaryOpen} onClick={() => setSummaryOpen(value => !value)}>{t('Production summary')} {summaryOpen ? '−' : '+'}</button>
    </div>
    {summaryOpen && <div className="mb-6"><AnalyticsPage /></div>}
    <Panel title={t('Inspection history')} action={<span className="text-xs text-text-muted">{t('Page')} {number(history.data?.pageNumber ?? page)}</span>}>
      {history.error && <ErrorNote>{history.error}</ErrorNote>}
      <form className="history-filters" onSubmit={search}>
        <input name="q" aria-label={t('Search class, bin, decision, product')} placeholder={t('Search class, bin, decision, product')} className="form-field" />
        <input name="productClass" aria-label={t('Class')} placeholder={t('Class')} className="form-field" />
        <select name="decision" aria-label={t('Decision')} className="form-field"><option value="">{t('Any decision')}</option><option value="PASS">{t('PASS')}</option><option value="REJECT">{t('REJECT')}</option><option value="MANUAL_REVIEW">{t('MANUAL_REVIEW')}</option></select>
        <select name="simulation" aria-label={t('Source')} className="form-field"><option value="">{t('Any source')}</option><option value="true">{t('SIMULATION')}</option><option value="false">{t('Not simulation')}</option></select>
        <button className="primary-button" type="submit">{t('Search')}</button>
      </form>
      <div className="relative overflow-x-auto">
        <table className="history-table">
          <thead><tr><th>{t('Time')}</th><th>{t('Product')}</th><th>{t('Decision')}</th><th>{t('Destination')}</th><th><span className="sr-only">{t('Record details')}</span></th></tr></thead>
          <tbody>{(history.data?.items ?? []).map(item => <Fragment key={item.inspectionId}>
            <tr>
              <td>{date(item.processedAt)}</td><td><span>{item.productClass}</span>{item.simulation && <span className="simulation-tag">{t('SIMULATION')}</span>}</td>
              <td><span className={`decision-badge ${item.decision === 'PASS' ? 'text-success-light' : item.decision === 'REJECT' ? 'text-danger-light' : 'text-warning-light'}`}>{text(item.decision)}</span></td>
              <td>{item.destinationBin}</td><td><button type="button" className="text-link" aria-expanded={expanded === item.inspectionId} aria-controls={`inspection-${item.inspectionId}`} onClick={() => setExpanded(expanded === item.inspectionId ? null : item.inspectionId)}>{t(expanded === item.inspectionId ? 'Hide details' : 'View details')}</button></td>
            </tr>
            {expanded === item.inspectionId && <tr id={`inspection-${item.inspectionId}`}><td colSpan={5}><InspectionDetails inspection={item} /></td></tr>}
          </Fragment>)}</tbody>
        </table>
        {history.data?.items.length === 0 && <p className="empty-state">{t('No stored inspections match these filters.')}</p>}
        {!history.data && !history.error && <p className="empty-state">{t('Waiting for data')}</p>}
      </div>
      <div className="mt-5 flex justify-between gap-2">
        <button type="button" disabled={page <= 1} className="utility-button" onClick={() => setPage(value => Math.max(1, value - 1))}>{t('Previous')}</button>
        <button type="button" disabled={!history.data?.hasNextPage} className="utility-button" onClick={() => setPage(value => value + 1)}>{t('Next')}</button>
      </div>
    </Panel>
  </div>
}
