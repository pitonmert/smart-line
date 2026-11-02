import assert from 'node:assert/strict'
import test from 'node:test'
import { en, tr } from '../src/i18n/catalog.ts'
import { formatDate, formatMessage, formatNumber, localize, readLanguage, saveLanguage, translate } from '../src/i18n/language.ts'
import { ApiError, getApiErrorMessage } from '../src/api/client.ts'

test('language defaults to English for absent, invalid or inaccessible storage', () => {
  for (const value of [null, '', 'de', 'TR', '{broken']) assert.equal(readLanguage(() => value), 'en')
  assert.equal(readLanguage(() => 'tr'), 'tr')
  assert.equal(readLanguage(() => 'en'), 'en')
  assert.equal(readLanguage(() => { throw new Error('Storage blocked') }), 'en')
  let stored: string | undefined
  saveLanguage('tr', value => { stored = value })
  assert.equal(stored, 'tr')
  assert.doesNotThrow(() => saveLanguage('en', () => { throw new Error('Storage blocked') }))
})

test('catalogs have complete translations with matching placeholders', () => {
  assert.deepEqual(Object.keys(en).sort(), Object.keys(tr).sort())
  for (const key of Object.keys(en) as (keyof typeof en)[]) {
    assert.ok(tr[key].trim(), key)
    assert.deepEqual([...en[key].matchAll(/\{\w+\}/g)].map(match => match[0]).sort(),
      [...tr[key].matchAll(/\{\w+\}/g)].map(match => match[0]).sort(), key)
  }
  assert.equal(translate('en', 'Control Room'), 'Control Room')
  assert.equal(translate('tr', 'Control Room'), 'Kontrol odası')
})

test('numeric messages and date display follow the selected locale', () => {
  assert.equal(formatNumber('en', 2480.5), '2,480.5')
  assert.equal(formatNumber('tr', 2480.5), '2.480,5')
  assert.equal(translate('tr', '{count} captures', { count: 2480 }), '2.480 çekim')
  assert.equal(translate('en', '{count} captures', { count: 2480 }), '2,480 captures')
  assert.equal(formatDate('tr', null), '—')
  assert.equal(formatDate('en', 'invalid timestamp'), 'invalid timestamp')
  assert.notEqual(formatDate('tr', '2026-11-01T10:05:00Z'), formatDate('en', '2026-11-01T10:05:00Z'))
})

test('known states and API errors translate while user text and identifiers survive', () => {
  assert.equal(localize('tr', 'ONLINE'), 'Çevrimiçi')
  assert.equal(localize('tr', 'NOT_CONFIGURED'), 'Yapılandırılmamış')
  assert.equal(localize('en', 'NOT_CONFIGURED'), 'Not configured')
  for (const value of ['BOX_RED', 'BIN_A', 'recipe-123', 'Operator note: check belt', 'constructor', '__proto__']) {
    assert.equal(localize('tr', value), value)
  }
  const error = new ApiError('original detail', 'CONVEYOR_MUST_BE_STOPPED', 409)
  assert.equal(localize('tr', getApiErrorMessage(error, 'fallback')), 'Hızı değiştirmeden önce konveyörü durdurun.')
  const unknown = new ApiError('Server-specific detail', 'CUSTOM_ERROR', 400)
  assert.equal(localize('tr', getApiErrorMessage(unknown, 'fallback')), 'Server-specific detail')
  assert.equal(localize('tr', getApiErrorMessage(new ApiError('Name', 'CUSTOM_ERROR', 400), 'fallback')), 'Name')
  assert.equal(localize('tr', getApiErrorMessage(new ApiError('Name', 'constructor', 400), 'fallback')), 'Name')
})

test('stored success notices can be rendered again in either language', () => {
  const notice = { key: '{code} is {stage}. {message}', params: { code: 'MOCK-1001', stage: 'SORTED', message: 'No hardware command was sent.' } } as const
  assert.equal(formatMessage('tr', notice), 'MOCK-1001 durumu: Ayrıldı. No hardware command was sent.')
  assert.equal(formatMessage('en', notice), 'MOCK-1001 is Sorted. No hardware command was sent.')
  assert.equal(formatMessage('en', null), '')
})
