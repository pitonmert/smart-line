import { useLanguage } from '../../i18n/LanguageContext'
import type { InspectionResult } from '../../types/inspection'

export default function InspectionDetails({ inspection }: { inspection: InspectionResult }) {
  const { t, text, number, date } = useLanguage()
  const rows = [
    [t('Color'), text(inspection.colorClass)], [t('Size'), text(inspection.sizeClass)],
    [t('Defects'), inspection.defectClasses.map(text).join(', ') || t('None')],
    [t('Quality'), text(inspection.qualityStatus)], [t('Confidence'), `${number(inspection.confidence * 100, { maximumFractionDigits: 1 })}%`],
    [t('Reasons'), inspection.reasons?.join(', ') || '—'], [t('Model'), [inspection.modelName, inspection.modelVersion].filter(Boolean).join(' · ') || '—'],
    [t('Recipe'), inspection.recipeVersion == null ? '—' : `v${inspection.recipeVersion}`],
    [t('Latency'), inspection.processingLatencyMs == null ? '—' : `${number(inspection.processingLatencyMs)} ms`],
    [t('Source'), inspection.simulation ? t('SIMULATION') : inspection.captureSource || inspection.cameraSource || t('Live observation')],
    [t('Visual size'), `${inspection.visualWidth == null ? '—' : number(inspection.visualWidth)} × ${inspection.visualHeight == null ? '—' : number(inspection.visualHeight)} px`],
    [t('Physical estimate'), `${inspection.estimatedPhysicalDimensions?.widthMm == null ? '—' : number(inspection.estimatedPhysicalDimensions.widthMm)} × ${inspection.estimatedPhysicalDimensions?.heightMm == null ? '—' : number(inspection.estimatedPhysicalDimensions.heightMm)} mm`],
    [t('Captured'), date(inspection.capturedAt)], [t('Processed'), date(inspection.processedAt)],
    ['ID', inspection.inspectionId],
  ]
  return <dl className="detail-list">{rows.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
}
