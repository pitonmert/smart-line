import assert from 'node:assert/strict'
import test from 'node:test'
import { mapSourceBox, normalizedRoiToSource } from '../src/lib/overlay.ts'

test('letterboxed wide panel keeps the source aspect and offset', () => {
  const mapped = mapSourceBox({ x: 10, y: 20, width: 30, height: 40 }, 240, 176, 480, 176)
  assert.equal(mapped.visible, true)
  assert.equal(mapped.x, 120 + 10)
  assert.equal(mapped.y, 20)
  assert.equal(mapped.width, 30)
  assert.equal(mapped.height, 40)
})

test('tall panel centers the image vertically', () => {
  const mapped = mapSourceBox({ x: 0, y: 0, width: 240, height: 176 }, 240, 176, 240, 352)
  assert.equal(mapped.y, 88)
  assert.equal(mapped.height, 176)
})

test('invalid source dimensions do not produce a box', () => {
  const mapped = mapSourceBox({ x: 1, y: 1, width: 2, height: 2 }, 0, 176, 100, 100)
  assert.equal(mapped.visible, false)
})

test('normalized ROI is converted in source pixels before scaling', () => {
  const source = normalizedRoiToSource({ x: 0.1, y: 0.1, width: 0.5, height: 0.5 }, 240, 176)
  const mapped = mapSourceBox(source, 240, 176, 240, 176)
  assert.equal(source.x, 24)
  assert.equal(mapped.x, 24)
  assert.equal(mapped.width, 120)
})
