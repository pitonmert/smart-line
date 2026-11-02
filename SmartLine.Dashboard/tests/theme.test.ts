import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readTheme, saveTheme } from '../src/theme/theme.ts'

test('theme defaults to dark and tolerates invalid or unavailable storage', () => {
  assert.equal(readTheme(() => null), 'dark')
  assert.equal(readTheme(() => 'unsupported'), 'dark')
  assert.equal(readTheme(() => { throw new Error('Unavailable') }), 'dark')
  assert.equal(readTheme(() => 'light'), 'light')
  assert.doesNotThrow(() => saveTheme('light', () => { throw new Error('Unavailable') }))
})
