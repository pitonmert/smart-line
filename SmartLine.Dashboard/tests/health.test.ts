import assert from 'node:assert/strict'
import test from 'node:test'
import { healthTone } from '../src/components/inspection/format.ts'

test('diagnostic states keep their meaning regardless of letter case', () => {
  assert.equal(healthTone('ONLINE'), 'good')
  assert.equal(healthTone('healthy'), 'good')
  assert.equal(healthTone('connected'), 'good')
  assert.equal(healthTone('NOT_CONFIGURED'), 'warn')
  assert.equal(healthTone('UNKNOWN'), 'warn')
  assert.equal(healthTone('not_loaded'), 'warn')
  assert.equal(healthTone('registered'), 'warn')
  assert.equal(healthTone('connecting'), 'warn')
  assert.equal(healthTone('disconnected'), 'bad')
  assert.equal(healthTone('OFFLINE'), 'bad')
  assert.equal(healthTone('unavailable'), 'bad')
  assert.equal(healthTone(undefined), 'neutral')
})
