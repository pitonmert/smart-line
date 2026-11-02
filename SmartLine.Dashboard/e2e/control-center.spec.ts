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
  if (path === '/api/simulation/status') {
    await route.fulfill(envelope(diagnostics.simulation))
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
  let handshakeReady = false
  let handshakeSent = false
  return async function fulfillSignalR(route: Route) {
    const request = route.request()
  expect(request.headers().authorization).toBeUndefined()
  expect(new URL(request.url()).pathname.startsWith('/api/auth')).toBe(false)
    const url = new URL(request.url())
    if (url.pathname.endsWith('/negotiate')) {
      initialPollSent = false
      handshakeReady = false
      handshakeSent = false
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
      handshakeReady = true
      await route.fulfill({ status: 200, contentType: 'text/plain', body: '' })
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
      const body = handshakeReady && !handshakeSent ? '{}' + recordSeparator : ''
      if (body) handshakeSent = true
      await route.fulfill({ status: 200, contentType: 'text/plain', body })
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

test('production is read-only and opens without authentication', async ({ page }) => {
  const errors = watchConsole(page)
  const writes: string[] = []
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') writes.push(request.url()) })
  await installBackend(page)
  await page.goto('/')
  await expect(page.getByText('SIMULATION MODE')).toBeVisible()
  await expect(page.getByRole('navigation').getByRole('link')).toHaveCount(3)
  await expect(page.getByRole('button', { name: /Start|Normal stop|Emergency stop|Inspect camera|Send product|Record inspection/ })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Last evaluation' })).toBeVisible()
  await expect(page.getByAltText('Live conveyor camera')).toBeVisible()
  expect(writes).toEqual([])
  expect(errors).toEqual([])
})

test('all management tools and history remain accessible', async ({ page }) => {
  const errors = watchConsole(page)
  await installBackend(page)
  await page.goto('/')
  const pages: [string, string][] = [
    ['Conveyor controls', 'Conveyor status'], ['Product Recipes', 'Recipes'], ['Calibration', 'Profiles'],
    ['Sensors', 'Time of flight'], ['System Diagnostics', 'Configuration'], ['Manual inspection', 'Last inspection'],
    ['Logical sorting', 'Sorting queue'], ['Dataset collection', 'Capture from camera'], ['Simulation tools', 'Inspection simulator'],
  ]
  for (const [navigation, marker] of pages) {
    await page.getByRole('link', { name: 'Management', exact: true }).click()
    await page.getByRole('link', { name: navigation, exact: false }).click()
    await expect(page.getByText(marker, { exact: true }).first()).toBeVisible()
    if (navigation === 'Dataset collection') {
      await expect(page.getByTestId('dataset-capture')).toBeVisible()
      await expect(page.getByRole('option', { name: 'Red box (BOX_RED)' })).toHaveCount(1)
    }
  }
  await page.getByRole('link', { name: 'History', exact: true }).click()
  await page.getByRole('button', { name: 'Production summary' }).click()
  await expect(page.getByRole('heading', { name: 'Inspection destinations' })).toBeVisible()
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
  await page.goto('/dataset')
  await expect(page.locator('input[type="file"]')).toHaveCount(0)
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
  await page.goto('/dataset')
  await expect(page.getByText('Dataset unavailable.')).toBeVisible()
  await expect(page.getByTestId('dataset-capture')).toBeDisabled()
})

test('language selection translates all sections and survives navigation and reload', async ({ page }) => {
  await installBackend(page)
  await page.route('**/api/control-room', route => route.fulfill(envelope({ ...controlRoom, totalCount: 2480 })))
  await page.goto('/')
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  await expect(page.getByText('2,480', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Türkçe', exact: true }).click()
  await expect(page).toHaveTitle('SmartLine Kontrol')
  await expect(page.getByText('2.480', { exact: true })).toBeVisible()
  await expect(page.getByText('22,0°C', { exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'Yönetim', exact: true }).click()
  const pages: [string, string][] = [
    ['Bant kontrolleri', 'Konveyör durumu'], ['Ürün reçeteleri', 'Reçeteler'], ['Kalibrasyon', 'Profiller'],
    ['Sensörler', 'Mesafe sensörü'], ['Sistem tanısı', 'Yapılandırılmamış'], ['Manuel inceleme', 'Son inceleme'],
    ['Mantıksal ayırma', 'Ayırma kuyruğu'], ['Veri kümesi toplama', 'Kameradan çek'], ['Simülasyon araçları', 'İnceleme simülatörü'],
  ]
  for (const [navigation, marker] of pages) {
    await page.getByRole('link', { name: navigation, exact: false }).click()
    await expect(page.getByText(marker, { exact: true }).first()).toBeVisible()
    await page.getByRole('button', { name: 'Yönetime dön', exact: false }).click()
  }
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
  await page.getByRole('button', { name: 'English', exact: true }).click()
  await page.getByRole('link', { name: 'Production', exact: true }).click()
  await expect(page.getByText('22.0°C', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => localStorage.getItem('smartline.language'))).toBe('en')
})

test('language and theme changes preserve forms and wire codes', async ({ page }) => {
  await installBackend(page)
  let submitted: Record<string, unknown> | undefined
  await page.route('**/api/inspections', async route => {
    if (route.request().method() !== 'POST') { await route.fulfill(envelope({ items: [], hasNextPage: false, pageNumber: 1, pageSize: 20 })); return }
    submitted = route.request().postDataJSON()
    await route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ isSuccess: false, code: 'PRODUCT_COLOR_INVALID', message: 'Original server detail' }) })
  })
  await page.goto('/inspection')
  await page.getByPlaceholder('Product class', { exact: true }).fill('BOX_RED')
  await page.getByPlaceholder('Color class', { exact: true }).fill('RED')
  await page.getByPlaceholder('Size class', { exact: true }).fill('MEDIUM')
  await page.getByRole('button', { name: 'Light', exact: true }).click()
  await page.getByRole('button', { name: 'Türkçe', exact: true }).click()
  await expect(page.getByPlaceholder('Ürün sınıfı', { exact: true })).toHaveValue('BOX_RED')
  await page.getByRole('button', { name: 'İnceleme kaydet', exact: true }).click()
  await expect(page.getByText('Ürün rengi geçerli değil.', { exact: true })).toBeVisible()
  expect(submitted).toMatchObject({ productClass: 'BOX_RED', colorClass: 'RED', sizeClass: 'MEDIUM', confidence: 0.8, simulation: false })
  await expect(page.getByPlaceholder('Ürün sınıfı', { exact: true })).toHaveValue('BOX_RED')
  await page.getByRole('button', { name: 'English', exact: true }).click()
  await expect(page.getByText('Invalid product color.', { exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'History', exact: true }).click()
  await page.getByRole('button', { name: 'Türkçe', exact: true }).click()
  await expect(page.locator('select[name="decision"] option[value="PASS"]')).toHaveText('KABUL')
  await expect(page.locator('select[name="decision"] option[value="REJECT"]')).toHaveText('RET')
})

for (const storage of ['invalid', 'blocked'] as const) {
  test(`language and theme selection handle ${storage} storage`, async ({ page }) => {
    await page.addInitScript(mode => {
      if (mode === 'invalid') { localStorage.setItem('smartline.language', 'unsupported'); localStorage.setItem('smartline.theme', 'unsupported') }
      else Object.defineProperty(window, 'localStorage', { get() { throw new Error('Storage blocked') } })
    }, storage)
    await installBackend(page)
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
    await page.getByRole('button', { name: 'Light', exact: true }).click()
    await page.getByRole('button', { name: 'Türkçe', exact: true }).click()
    await expect(page.getByRole('link', { name: 'Üretim', exact: true })).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  })
}

test('recipe names and unknown server messages are not translated', async ({ page }) => {
  await installBackend(page)
  await page.route('**/api/recipes', route => route.fulfill(envelope([{ ...recipe, name: 'Environment' }])))
  await page.goto('/recipes')
  await page.getByRole('button', { name: 'Türkçe', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Environment', exact: true })).toBeVisible()
  await page.route('**/api/dataset/sessions', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ isSuccess: false, code: 'CUSTOM_ERROR', message: 'Name' }) }))
  await page.getByRole('link', { name: 'Yönetim', exact: true }).click()
  await page.getByRole('link', { name: 'Veri kümesi toplama', exact: false }).click()
  await expect(page.getByText('Name', { exact: true })).toBeVisible()
})

test('theme persists and all language/theme combinations work', async ({ page }) => {
  await installBackend(page)
  await page.goto('/')
  for (const language of ['Türkçe', 'English']) {
    for (const theme of ['light', 'dark']) {
      await page.getByRole('button', { name: language, exact: true }).click()
      const tr = language === 'Türkçe'
      await page.getByRole('button', { name: theme === 'dark' ? tr ? 'Koyu' : 'Dark' : tr ? 'Açık' : 'Light', exact: true }).click()
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await page.getByRole('link', { name: tr ? 'Geçmiş' : 'History', exact: true }).click()
      await expect(page.getByRole('heading', { name: tr ? 'İnceleme geçmişi' : 'Inspection history' })).toBeVisible()
    }
  }
  await page.getByRole('button', { name: 'Light', exact: true }).click()
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  expect(await page.evaluate(() => localStorage.getItem('smartline.theme'))).toBe('light')
})

test('legacy routes redirect and browser history stays usable', async ({ page }) => {
  await installBackend(page)
  const aliases: Record<string, string> = { inspection: 'inspection', sensors: 'sensors', sorting: 'sorting', recipes: 'recipes', calibration: 'calibration', dataset: 'dataset', diagnostics: 'diagnostics' }
  for (const [old, next] of Object.entries(aliases)) {
    await page.goto('/' + old)
    await expect(page).toHaveURL('/management/' + next)
  }
  await page.goto('/analytics')
  await expect(page).toHaveURL('/history')
  await page.getByRole('link', { name: 'Management', exact: true }).click()
  await page.getByRole('link', { name: 'Product Recipes', exact: false }).click()
  await page.goBack()
  await expect(page).toHaveURL('/management')
  await page.goForward()
  await expect(page).toHaveURL('/management/recipes')
})

test('loading failures do not fabricate zero counts and preserve known values after a disconnect', async ({ page }) => {
  await installBackend(page)
  let failed = true
  await page.route('**/api/control-room', route => route.fulfill(failed ? { status: 503, body: '' } : envelope({ ...controlRoom, totalCount: 2480 })))
  await page.goto('/')
  const counters = page.getByRole('definition')
  await expect(page.getByText('Connection lost · showing last known values')).toBeVisible()
  await expect(page.locator('.production-counters dd').first()).toHaveText('—')
  failed = false
  await expect(page.getByText('2,480', { exact: true })).toBeVisible({ timeout: 6000 })
  failed = true
  await expect(page.getByText('Connection lost · showing last known values')).toBeVisible({ timeout: 6000 })
  await expect(page.locator('.production-counters dd').first()).toHaveText('2,480')
  expect(await counters.count()).toBeGreaterThan(0)
})

test('conveyor controls require a live known device and retain error feedback', async ({ page }) => {
  await installBackend(page)
  let device = 'unknown'
  await page.route('**/api/diagnostics', route => route.fulfill(envelope({ ...diagnostics, esp32: device })))
  await page.goto('/management/control')
  const start = page.getByRole('button', { name: 'Start', exact: true })
  await expect(start).toBeDisabled()
  device = 'online'
  await expect(start).toBeEnabled({ timeout: 8000 })
  let fail = true
  await page.route('**/api/conveyor/start', route => route.fulfill(fail ? { status: 503, contentType: 'application/json', body: JSON.stringify({ isSuccess: false, code: 'MQTT_UNAVAILABLE', message: '' }) } : envelope({ ...conveyor, isRunning: true })))
  await start.click()
  await expect(page.getByText('The device connection is unavailable. The operation was not performed.', { exact: true })).toBeVisible()
  fail = false
  await start.click()
  await expect(page.getByRole('button', { name: 'Normal stop', exact: true })).toBeEnabled()
  device = 'stale'
  await expect(page.getByRole('button', { name: 'Normal stop', exact: true })).toBeDisabled({ timeout: 8000 })
})

test('history filters and expandable inspection metadata preserve API semantics', async ({ page }) => {
  await installBackend(page)
  const inspection = { inspectionId: 'inspection-1', productId: 'product-1', recipeId: recipe.id, capturedAt: '2026-10-06T00:00:00Z', processedAt: '2026-10-06T00:00:01Z', productClass: 'BOX_RED', colorClass: 'RED', sizeClass: 'MEDIUM', defectClasses: [], qualityStatus: 'OK', confidence: 0.9, boundingBoxes: [], decision: 'PASS', destinationBin: 'BIN_A', simulation: true, reasons: ['MATCHED_RULE'], recipeVersion: 1, modelVersion: 'v1', processingLatencyMs: 23 }
  let query = ''
  await page.route('**/api/inspections?*', route => { query = new URL(route.request().url()).search; return route.fulfill(envelope({ items: [inspection], hasNextPage: true, pageNumber: 1, pageSize: 20 })) })
  await page.goto('/history')
  await expect(page.getByText('BOX_RED', { exact: true })).toBeVisible()
  await expect(page.getByText('MATCHED_RULE', { exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'View details', exact: true }).click()
  await expect(page.getByText('MATCHED_RULE', { exact: true })).toBeVisible()
  await page.getByRole('combobox', { name: 'Decision', exact: true }).selectOption('PASS')
  await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption('true')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect.poll(() => query).toContain('decision=PASS')
  expect(query).toContain('simulation=true')
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect.poll(() => query).toContain('pageNumber=2')
})

test('inactive tools do not poll or continue work on the production screen', async ({ page }) => {
  await installBackend(page)
  const calls: string[] = []
  page.on('request', request => { const path = new URL(request.url()).pathname; if (/dataset|simulation|calibration-profiles|tracking/.test(path)) calls.push(path) })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Last evaluation' })).toBeVisible()
  await page.getByRole('link', { name: 'Management', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Tools', exact: true })).toBeVisible()
  expect(calls).toEqual([])
})

test('hub loss disables controls and reconnection restores them', async ({ page }) => {
  await installBackend(page)
  await page.route('**/api/diagnostics', route => route.fulfill(envelope({ ...diagnostics, esp32: 'online' })))
  await page.goto('/management/control')
  const start = page.getByRole('button', { name: 'Start', exact: true })
  await expect(start).toBeEnabled()
  const disconnect = (route: Route) => route.abort('internetdisconnected')
  await page.route('**/hubs/dashboard**', disconnect)
  await expect(start).toBeDisabled({ timeout: 10000 })
  await page.unroute('**/hubs/dashboard**', disconnect)
  await expect(start).toBeEnabled({ timeout: 15000 })
})

for (const viewport of [{ width: 1920, height: 1080 }, { width: 1366, height: 768 }, { width: 390, height: 844 }]) {
  test(`responsive layout stays within ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport)
    await installBackend(page)
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Last evaluation' })).toBeVisible()
    await expect(page.locator('.line-summary').getByText('Live', { exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width)
    if (viewport.width === 1920) expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeLessThanOrEqual(viewport.height)
    await page.getByRole('link', { name: 'Management', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Tools', exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width)
  })
}

test('leaving dataset collection stops browser interval capture', async ({ page }) => {
  await installBackend(page)
  let captures = 0
  const dataset = { sessionId: 'session-interval', recipeId: recipe.id, recipeName: 'DemoBox', status: 'ACTIVE', productClass: 'BOX_RED', qualityLabel: 'GOOD', colorLabel: 'RED', sizeLabel: 'MEDIUM', defectLabels: [], captureMode: 'interval', intervalMs: 200, imageCount: 0, classCounts: {}, qualityCounts: {}, defectCounts: {}, recentSamples: [], startedAt: '2026-10-06T00:00:00Z', simulation: false, labelStatus: 'class-labeled' }
  await page.route('**/api/dataset/sessions', route => route.fulfill(envelope([dataset])))
  await page.route('**/api/dataset/sessions/session-interval/captures/camera', route => {
    captures++
    return route.fulfill(envelope({ session: { ...dataset, imageCount: captures }, sample: { sourceWidth: 640, sourceHeight: 480, captureSource: 'esp32_capture' } }))
  })
  await page.goto('/management/dataset')
  await expect(page.locator('.line-summary').getByText('Live', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Start interval (200 ms)', exact: true }).click()
  await expect.poll(() => captures).toBeGreaterThan(0)
  await page.getByRole('link', { name: 'Production', exact: true }).click()
  const atExit = captures
  // Observe several former capture periods; no tool is mounted on Production.
  await page.waitForTimeout(800)
  expect(captures).toBe(atExit)
})

test('fire reporting remains global and safety interlocks survive the new layout', async ({ page }) => {
  await installBackend(page)
  const fire = { id: 7, alertType: 'FIRE', isActive: true, triggeredAt: '2026-10-06T00:00:00Z' }
  await page.route('**/api/dashboard', route => route.fulfill(envelope({ conveyorStatus: { ...conveyor, isEmergencyStopped: true, isFireDetected: true }, todayStatistics: null, recentProducts: [], activeAlerts: [fire] })))
  await page.route('**/api/diagnostics', route => route.fulfill(envelope({ ...diagnostics, esp32: 'online' })))
  await page.goto('/')
  await expect(page.getByText('FIRE ALARM', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Resolve alarm', exact: true })).toHaveCount(0)
  await page.getByRole('link', { name: 'History', exact: true }).click()
  await expect(page.getByText('FIRE ALARM', { exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'Management', exact: true }).click()
  await page.getByRole('link', { name: 'Conveyor controls', exact: false }).click()
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Reset latch', exact: true })).toBeDisabled()
})
