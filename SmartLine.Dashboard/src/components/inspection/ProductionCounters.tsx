import { useLanguage } from '../../i18n/LanguageContext'
import { usePlant } from '../../context/PlantContext'

export default function ProductionCounters() {
  const { room } = usePlant()
  const { t, number } = useLanguage()
  const data = room.data
  const metrics = [
    ['Total', data?.totalCount, ''], ['Accepted', data?.acceptedCount, 'text-success-light'],
    ['Rejected', data?.rejectedCount, 'text-danger-light'], ['Manual review', data?.manualReviewCount, 'text-warning-light'],
    ['Yield', data?.yieldPercent, ''],
  ] as const
  return <dl className="production-counters" aria-label={t('Production summary')}>
    {metrics.map(([label, value, tone]) => <div key={label}><dt>{t(label)}</dt><dd className={tone}>{value == null ? '—' : `${number(value)}${label === 'Yield' ? '%' : ''}`}</dd></div>)}
  </dl>
}
