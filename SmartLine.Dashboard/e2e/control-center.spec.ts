import { expect, test, type Page, type Route } from '@playwright/test'

const recordSeparator = '\u001e'

const conveyor = {
  speed: 0,
  isRunning: false,
  isEmergencyStopped: false,
  isFireDetected: false,
  fireDeviceEpoch: 'e2e',
  lastFireGeneration: 0,
  isFireGenerationFault: false,
  temperature: 22,
  humidity: 40,
  fanActive: false,
  lastUpdated: '2026-10-06T00:00:00Z',
}

const recipe = {
  id: 'recipe-1',
  name: 'DemoBox',
  description: 'Browser test recipe',
  isActive: true,
  version: 1,
  updatedAt: '2026-10-06T00:00:00Z',
  definition: {
    name: 'DemoBox',
    productClasses: [
      { code: 'BOX_RED', displayName: 'Red box', expectedColor: 'RED' },
      { code: 'BOX_BLUE', displayName: 'Blue box', expectedColor: 'BLUE' },
      { code: 'BOX_GREEN', displayName: 'Green box', expectedColor: 'GREEN' },
    ],
    allowedColors: [
      { code: 'RED', displayName: 'Red', hueMin: 345, hueMax: 15 },
      { code: 'BLUE', displayName: 'Blue', hueMin: 190, hueMax: 250 },
      { code: 'GREEN', displayName: 'Green', hueMin: 80, hueMax: 160 },
    ],
    sizeClasses: [
      { code: 'SMALL', displayName: 'Small', minWidthMm: 0, maxWidthMm: 40 },
      { code: 'MEDIUM', displayName: 'Medium', minWidthMm: 40, maxWidthMm: 80 },
      { code: 'LARGE', displayName: 'Large', minWidthMm: 80, maxWidthMm: 200 },
    ],
    defectClasses: [{ code: 'DAMAGED', displayName: 'Damaged', rejects: true }],
    acceptance: { minimumConfidence: 0.6, defaultDestinationBin: 'MANUAL_REVIEW' },
    sortingRules: [],
    destinationBins: [{ code: 'BIN_A', displayName: 'Bin A', role: 'PASS' }],
    model: { task: 'detect', family: 'YOLO26', name: 'yolo26n', version: 'untrained', inputSize: 640 },
    cameraProfile: {
      streamUrl: 'http://192.168.1.50:81/stream',
      sourceWidth: 240,
      sourceHeight: 176,
      sensor: 'GC2145',
      pixelFormat: 'RGB565',
      transport: 'MJPEG',
    },
    roi: { x: 0.1, y: 0.1, width: 0.8, height: 0.8 },
    inspection: { continuousInference: false, triggerModes: ['manual'], selectionStrategy: 'latest' },
  },
}

const diagnostics = {
  api: 'healthy',
  database: 'healthy',
  mqtt: 'connected',
  vision: 'NOT_CONFIGURED',
  camera: 'UNKNOWN',
  lastInspectionAt: null,
  modelStatus: 'not_loaded',
  modelName: 'yolo26n',
  modelVersion: 'untrained',
  esp32: 'unknown',
  signalR: 'hub_enabled',
  simulation: { enabled: true, active: true, latched: false, inFlightSimulatedProducts: 0 },
  hardwareActuationEnabled: false,
  environment: 'Development',
  activeRecipe: 'DemoBox',
  timeOfFlightSource: 'NOT_INTEGRATED',
  cameraStreamUrl: 'http://192.168.1.50:81/stream',
  lastFrameAt: null,
  frameWidth: null,
  frameHeight: null,
  cameraConsecutiveFailures: null,
  cameraStreamHost: '192.168.1.50:81',
  model: {
    status: 'not_loaded',
    name: 'yolo26n',
    version: 'untrained',
    configured: false,
    loaded: false,
    backend: null,
    task: 'detect',
    artifact: null,
    lastInferenceLatencyMs: null,
    loadError: null,
    confidenceThreshold: 0.25,
  },
}

const controlRoom = {
  activeRecipe: 'DemoBox',
  currentProduct: null,
  lastInspection: null,
  totalCount: 0,
  acceptedCount: 0,
  rejectedCount: 0,
  manualReviewCount: 0,
  yieldPercent: null,
  binCounts: [{ destinationBin: 'BIN_A', count: 0 }],
  recentEvents: [],
  simulationActive: true,
}

const tinyJpeg = Buffer.from(
  '/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFwABAQEBAAAAAAAAAAAAAAAAAAIDBP/EABQBAQAAAAAAAAAAAAAAAAAAAAD/xAAUAQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAGf/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPwF/f//Z',
  'base64',
)

function envelope(data: unknown, status = 200) {
  return {
    status,
    contentType: 'application/json',
    body: JSON.stringify({ isSuccess: status < 400, message: status < 400 ? '' : 'Request was rejected.', code: status < 400 ? undefined : 'REQUEST_INVALID', data }),
  }
}

async function fulfillApi(route: Route) {
  const request = route.request()
  expect(request.headers().authorization).toBeUndefined()
  expect(new URL(request.url()).pathname.startsWith('/api/auth')).toBe(false)
  const url = new URL(request.url())
  const path = url.pathname
  if (path === '/api/dashboard') {
    await route.fulfill(envelope({ conveyorStatus: conveyor, todayStatistics: null, recentProducts: [], activeAlerts: [] }))
    return
  }
  if (path === '/api/camera/preview') {
    await route.fulfill({ status: 200, contentType: 'image/jpeg', body: tinyJpeg })
    return
  }
  if (path === '/api/diagnostics') {
    await route.fulfill(envelope(diagnostics))
    return
  }
  if (path === '/api/control-room') {
    await route.fulfill(envelope(controlRoom))
    return
  }
  if (path === '/api/recipes') {
    await route.fulfill(envelope([recipe]))
    return
  }
  if (path === '/api/calibration-profiles') {
    await route.fulfill(envelope([]))
    return
  }
  if (path === '/api/tracking/queue' || path === '/api/dataset/sessions' || path === '/api/statistics/hourly') {
    await route.fulfill(envelope([]))
    return
  }
  if (path === '/api/inspections') {
    await route.fulfill(envelope({ items: [], hasNextPage: false, pageNumber: 1, pageSize: 20 }))
    return
  }
  await route.fulfill(envelope([]))
}

function createSignalR() {
  // The first long-poll must be HTTP 200 or the client treats the transport as failed.
  // Later polls stay 200 with an empty body. HTTP 204 terminates the connection.
  let initialPollSent = false
  return async function fulfillSignalR(route: Route) {
    const request = route.request()
  expect(request.headers().authorization).toBeUndefined()
  expect(new URL(request.url()).pathname.startsWith('/api/auth')).toBe(false)
    const url = new URL(request.url())
    if (url.pathname.endsWith('/negotiate')) {
      initialPollSent = false
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          negotiateVersion: 1,
          connectionId: 'e2e-connection',
          connectionToken: 'e2e-token',
          availableTransports: [{ transport: 'LongPolling', transferFormats: ['Text', 'Binary'] }],
        }),
      })
      return
    }
    const posted = request.postData() ?? ''
    if (request.method() === 'POST' && posted.includes('protocol')) {
      await route.fulfill({ status: 200, contentType: 'text/plain', body: `{}` + recordSeparator })
      return
    }
    if (request.method() === 'DELETE') {
      await route.fulfill({ status: 200, body: '' })
      return
    }
    const firstPoll = !initialPollSent
    initialPollSent = true
    if (!firstPoll) {
      await new Promise(resolve => setTimeout(resolve, 400))
    }
    try {
      await route.fulfill({ status: 200, contentType: 'text/plain', body: '' })
    } catch {
      // The page closed or the client aborted the poll.
    }
  }
}

async function installBackend(page: Page) {
  const fulfillSignalR = createSignalR()
  await page.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (url.pathname.startsWith('/hubs/dashboard')) {
      await fulfillSignalR(route)
      return
    }
    if (url.pathname.startsWith('/api/')) {
      await fulfillApi(route)
      return
    }
    if (url.hostname === '192.168.1.50') {
      await route.fulfill({ status: 200, contentType: 'image/jpeg', body: tinyJpeg })
      return
    }
    await route.continue()
  })
}

function watchConsole(page: Page) {
  const errors: string[] = []
  page.on('console', message => {
    if (message.type() !== 'error') return
    const text = message.text()
    if (text.includes('favicon') || text.includes('401')) return
    errors.push(text)
  })
  page.on('pageerror', error => errors.push(error.message))
  return errors
}

test('control center opens without login or authentication requests', async ({ page }) => {
  const errors = watchConsole(page)
  await installBackend(page)
  await page.goto('/')
  await expect(page.getByText('SIMULATION MODE')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Giriş yap' })).toHaveCount(0)
  expect(errors).toEqual([])
})

test('control center renders every main page without credentials', async ({ page }) => {
  const errors = watchConsole(page)
  await installBackend(page)
  await page.goto('/')

  await expect(page.getByText('SIMULATION MODE')).toBeVisible()
  await expect(page.getByText('Simulator values are on screen.')).toBeVisible()
  const camera = page.getByAltText('Live conveyor camera')
  await expect(camera).toBeVisible()
  const box = await camera.boundingBox()
  expect(box?.height ?? 0).toBeGreaterThan(200)

  const pages: [string, string][] = [
    ['AI Inspection', 'Last inspection'],
    ['Production Analytics', 'Inspection destinations'],
    ['Sensors', 'Time of flight'],
    ['Sorting', 'Actuation'],
    ['Product Recipes', 'DemoBox'],
    ['Calibration', 'Profiles'],
    ['Dataset & AI', 'Capture from camera'],
    ['Inspection History', 'Inspection history'],
    ['System Diagnostics', 'NOT_CONFIGURED'],
    ['Control Room', 'SIMULATION MODE'],
  ]
  for (const [navigation, marker] of pages) {
    await page.getByRole('button', { name: navigation }).first().click()
    await expect(page.getByText(marker).first()).toBeVisible()
    if (navigation === 'Dataset & AI') {
      await expect(page.getByTestId('dataset-capture')).toBeVisible()
      await expect(page.getByRole('option', { name: 'Red box (BOX_RED)' })).toHaveCount(1)
    }
    if (navigation === 'System Diagnostics') {
      await expect(page.getByText('UNKNOWN').first()).toBeVisible()
      await expect(page.getByText('NOT_INTEGRATED')).toBeVisible()
    }
  }
  expect(errors).toEqual([])
})

test('a remotely stopped dataset session replaces the local capture result', async ({ page }) => {
  await installBackend(page)
  let active = true
  let captures = 0
  const dataset = { sessionId: 'session-1', recipeId: recipe.id, recipeName: 'DemoBox', status: 'ACTIVE', productClass: 'BOX_RED', qualityLabel: 'GOOD', colorLabel: 'RED', sizeLabel: 'LARGE', defectLabels: [], captureMode: 'manual', imageCount: 0, classCounts: {}, qualityCounts: {}, defectCounts: {}, recentSamples: [], startedAt: '2026-10-06T00:00:00Z', simulation: false, labelStatus: 'class-labeled' }
  await page.route('**/api/dataset/sessions', route => route.fulfill(envelope(active ? [dataset] : [])))
  await page.route('**/api/dataset/sessions/session-1/captures/camera', async route => {
    captures++
    await route.fulfill(envelope({ session: { ...dataset, imageCount: captures }, sample: { sourceWidth: 640, sourceHeight: 480, captureSource: 'esp32_capture' } }))
    active = false
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Dataset & AI' }).click()
  await expect(page.locator('input[type="file"]')).toHaveCount(0)
  await expect(page.getByText('Import from disk', { exact: true })).toHaveCount(0)
  const capture = page.getByTestId('dataset-capture')
  await expect(capture).toBeEnabled()
  await capture.click()
  await expect(page.getByText('No dataset session is active.', { exact: false })).toBeVisible({ timeout: 8000 })
  await expect(capture).toBeDisabled()
  expect(captures).toBe(1)
})

test('a dataset polling failure disables capture and is visible', async ({ page }) => {
  await installBackend(page)
  await page.route('**/api/dataset/sessions', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ isSuccess: false, message: 'Dataset unavailable.', code: 'SERVICE_UNAVAILABLE' }) }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Dataset & AI' }).click()
  await expect(page.getByText('Dataset unavailable.')).toBeVisible()
  await expect(page.getByTestId('dataset-capture')).toBeDisabled()
})
