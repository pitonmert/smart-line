import { catalogs, type MessageKey } from './catalog.ts'

export type Language = 'en' | 'tr'
export type DisplayText = string | { raw: string }
export type MessageParams = Record<string, string | number>
export interface Message { key: MessageKey; params?: MessageParams }
export const languageStorageKey = 'smartline.language'
export const locales: Record<Language, string> = { en: 'en-US', tr: 'tr-TR' }

export function readLanguage(read = () => localStorage.getItem(languageStorageKey)): Language {
  try { return read() === 'tr' ? 'tr' : 'en' } catch { return 'en' }
}

export function saveLanguage(language: Language, write = (value: string) => localStorage.setItem(languageStorageKey, value)) {
  try { write(language) } catch { /* Keep the current selection when storage is unavailable. */ }
}

export function translate(language: Language, key: MessageKey, params: MessageParams = {}) {
  return catalogs[language][key].replace(/\{(\w+)\}/g, (placeholder, name: string) => {
    const value = params[name]
    return value === undefined ? placeholder : typeof value === 'number' ? formatNumber(language, value) : value
  })
}

export function formatNumber(language: Language, value: number, options?: Intl.NumberFormatOptions) {
  return new Intl.NumberFormat(locales[language], options).format(value)
}

export function formatDate(language: Language, value?: string | null, timeOnly = false) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return new Intl.DateTimeFormat(locales[language], timeOnly
    ? { timeStyle: 'medium' }
    : { dateStyle: 'short', timeStyle: 'medium' }).format(date)
}

const statuses: Record<string, MessageKey> = {
  high_temperature: 'High temperature', fire: 'Fire alarm', emergency_stop: 'Emergency stop', no_barcode_detected: 'Barcode unreadable',
  healthy: 'Healthy', connected: 'Connected', online: 'Online', offline: 'Offline', unknown: 'Unknown',
  not_configured: 'Not configured', not_loaded: 'Not loaded', not_integrated: 'Not integrated',
  checking: 'Checking', connecting: 'Connecting…', reconnecting: 'Reconnecting', disconnected: 'Disconnected',
  registered: 'Registered', stale: 'Stale', active: 'Active', stopped: 'Stopped', queued: 'Queued', sorted: 'Sorted',
  failed: 'Failed', manual: 'Manual', interval: 'Interval', model: 'Model observation', simulation: 'SIMULATION',
  manual_review: 'Manual review', enabled: 'Enabled', disabled: 'Disabled', true: 'Yes', false: 'No',
  none: 'None', available: 'Available', unavailable: 'Unavailable', mock: 'Mock',
  preview: 'Preview', inspection: 'Inspection', dataset: 'Dataset',
}

export function localize(language: Language, value: DisplayText | null | undefined): string {
  if (!value) return value ?? '—'
  if (typeof value !== 'string') return value.raw
  const normalized = value.toLowerCase()
  const key = Object.hasOwn(catalogs.en, value) ? value as MessageKey
    : Object.hasOwn(statuses, normalized) ? statuses[normalized] : undefined
  return key ? translate(language, key) : value
}

export function formatMessage(language: Language, message: Message | null): string {
  if (!message) return ''
  const params = Object.fromEntries(Object.entries(message.params ?? {}).map(([key, value]) => [
    key,
    typeof value === 'string' && ['stage', 'decision', 'size', 'source', 'fallback'].includes(key) ? localize(language, value) : value,
  ]))
  return translate(language, message.key, params)
}
