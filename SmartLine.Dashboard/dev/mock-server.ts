import { randomUUID } from 'node:crypto'
import type { ServerResponse } from 'node:http'
import type { Plugin } from 'vite'
import type { DashboardDto, StatisticsDto } from '../src/types/index.ts'
import type { CalibrationProfile, DatasetSession, DiagnosticsReport, InspectionResult, ProductTrack, RecipeSummary } from '../src/types/inspection.ts'

const now = Date.now()
const time = (minutes = 0) => new Date(now - minutes * 60_000).toISOString()
const colors = ['RED', 'BLUE', 'GREEN']
const recipe: RecipeSummary = {
  id: 'mock-recipe', name: 'DemoBox', description: 'Example recipe for the mock preview.',
  version: 1, isActive: true, updatedAt: time(),
  definition: {
    name: 'DemoBox',
    productClasses: colors.map(color => ({ code: `BOX_${color}`, displayName: `${color} box`, expectedColor: color })),
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
    acceptance: { minimumConfidence: 0.8, defaultDestinationBin: 'MANUAL_REVIEW' },
    sortingRules: [{ id: 'damaged', priority: 1, destinationBin: 'REJECT_BIN', when: { defectClass: 'DAMAGED' } }],
    destinationBins: [
      { code: 'BIN_A', displayName: 'Accepted', role: 'PASS' },
      { code: 'REJECT_BIN', displayName: 'Rejected', role: 'REJECT' },
      { code: 'MANUAL_REVIEW', displayName: 'Manual review', role: 'HOLD' },
    ],
    model: { task: 'detect', family: 'YOLO26', name: 'demo-model', version: 'mock', inputSize: 640 },
    cameraProfile: { streamUrl: '/api/camera/preview', sourceWidth: 640, sourceHeight: 480, sensor: 'MOCK', pixelFormat: 'RGB', transport: 'MOCK' },
    roi: { x: 0.1, y: 0.15, width: 0.8, height: 0.7 },
    inspection: { continuousInference: false, triggerModes: ['manual'], selectionStrategy: 'latest' },
  },
}

const inspections: InspectionResult[] = Array.from({ length: 12 }, (_, index) => {
  const rejected = index % 4 === 1
  const review = index % 4 === 2
  return {
    inspectionId: `mock-inspection-${index}`, productId: `mock-product-${index}`, recipeId: recipe.id,
    capturedAt: time(index), processedAt: time(index), productClass: `BOX_${colors[index % 3]}`,
    colorClass: colors[index % 3], sizeClass: 'MEDIUM', defectClasses: rejected ? ['DAMAGED'] : [],
    qualityStatus: rejected ? 'DEFECTIVE' : review ? 'MANUAL_REVIEW' : 'GOOD', confidence: review ? 0.67 : 0.96,
    boundingBoxes: [{ x: 190, y: 150, width: 220, height: 170, label: `BOX_${colors[index % 3]}`, confidence: 0.96 }],
    geometry: { sourceWidth: 640, sourceHeight: 480 }, visualWidth: 220, visualHeight: 170,
    estimatedPhysicalDimensions: { widthMm: 55, heightMm: 42.5, method: 'mock', depthAvailable: false },
    modelName: 'demo-model', modelVersion: 'mock', decision: rejected ? 'REJECT' : review ? 'MANUAL_REVIEW' : 'PASS',
    destinationBin: rejected ? 'REJECT_BIN' : review ? 'MANUAL_REVIEW' : 'BIN_A',
    processingLatencyMs: 42, simulation: false, recipeVersion: 1,
  }
})
const queue: ProductTrack[] = inspections.slice(0, 3).map((inspection, index) => ({
  productId: inspection.productId, publicCode: `MOCK-${1001 + index}`, recipeId: recipe.id,
  inspectionId: inspection.inspectionId, stage: 'QUEUED', simulation: false,
  destinationBin: inspection.destinationBin, decision: inspection.decision, detectedAt: inspection.capturedAt,
  queuedAt: inspection.processedAt, sortDueAt: time(-1),
}))
const statistics: StatisticsDto = {
  id: 1, date: time().slice(0, 10), hour: null, totalProducts: 2450, defectiveProducts: 90,
  redProducts: 1000, blueProducts: 800, greenProducts: 650, yellowProducts: 0, defectiveRate: 3.7,
}
const dashboard: DashboardDto = {
  conveyorStatus: { speed: 55, isRunning: true, isEmergencyStopped: false, isFireDetected: false,
    fireDeviceEpoch: 'mock', lastFireGeneration: 0, isFireGenerationFault: false,
    temperature: 26.4, humidity: 46, fanActive: false, lastUpdated: time() },
  todayStatistics: statistics,
  recentProducts: inspections.slice(0, 5).map((item, index) => ({ id: index + 1, barcode: `MOCK-${1001 + index}`,
    color: item.colorClass, status: item.decision === 'PASS' ? 'OK' : 'DEFECTIVE', passedAt: item.processedAt })),
  activeAlerts: [{ id: 1, alertType: 'HIGH_TEMPERATURE', isActive: true, triggeredAt: time(5), resolvedAt: null }],
}
const calibration: CalibrationProfile = {
  id: 'mock-calibration', name: 'Demo camera calibration', description: 'Example values; no hardware was calibrated.',
  isActive: true, version: 1, simulationDataPresent: false, updatedAt: time(),
  definition: { sourceWidth: 640, sourceHeight: 480, pixelsPerMillimeter: 4, orientationDegrees: 0,
    flipHorizontal: false, flipVertical: false, gateMappings: [], tofBaseline: { source: 'NOT_INTEGRATED' },
    hardwareActuationEnabled: false, hardwareActuationConfirmed: false },
}
const simulation = { enabled: false, active: false, latched: false, inFlightSimulatedProducts: 0 }
const diagnostics: DiagnosticsReport = {
  api: 'MOCK', database: 'MOCK', mqtt: 'MOCK', vision: 'MOCK', camera: 'MOCK', esp32: 'MOCK', signalR: 'MOCK',
  lastInspectionAt: time(), modelStatus: 'mock', modelName: 'demo-model', modelVersion: 'mock', simulation,
  hardwareActuationEnabled: false, environment: 'Mock preview', activeRecipe: recipe.name,
  timeOfFlightSource: 'NOT_INTEGRATED', cameraStreamUrl: '/api/camera/preview', frameWidth: 640, frameHeight: 480,
  model: { status: 'mock', configured: false, loaded: false, task: 'detect', loadError: 'No model is running in mock mode.' },
}
const dataset: DatasetSession = {
  sessionId: 'mock-session', recipeId: recipe.id, recipeName: recipe.name, status: 'ACTIVE',
  productClass: 'BOX_RED', qualityLabel: 'GOOD', colorLabel: 'RED', sizeLabel: 'MEDIUM', defectLabels: [],
  captureMode: 'manual', imageCount: 3, classCounts: { BOX_RED: 3 }, qualityCounts: { GOOD: 3 }, defectCounts: {},
  startedAt: time(20), simulation: false, labelStatus: 'Mock samples; bounding boxes are not annotated.',
  recentSamples: [0, 1, 2].map(index => ({ sampleId: `mock-sample-${index}`, fileName: `mock-${index}.svg`,
    capturedAt: time(index), productClass: 'BOX_RED', qualityLabel: 'GOOD', colorLabel: 'RED', sizeLabel: 'MEDIUM',
    defectLabels: [], trigger: 'manual', frameRole: 'dataset', sourceWidth: 640, sourceHeight: 480, captureSource: 'mock' })),
}
const camera = `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="480" viewBox="0 0 640 480">
<rect width="640" height="480" fill="#111827"/><path d="M0 130h640v220H0z" fill="#334155"/>
<path d="M0 160h640M0 320h640" stroke="#64748b" stroke-width="5"/>
<rect x="190" y="155" width="220" height="165" rx="8" fill="#dc2626"/>
<path d="M300 155v165" stroke="#fca5a5" stroke-width="20"/>
<g fill="#e2e8f0" font-family="sans-serif" text-anchor="middle"><text x="320" y="55" font-size="24">MOCK CAMERA</text>
<text x="320" y="410" font-size="18">Illustration only - no live camera</text></g></svg>`

function json(response: ServerResponse, data: unknown, status = 200) {
  response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
  response.end(JSON.stringify(data))
}

export function mockServer(): Plugin {
  return {
    name: 'dashboard-mock-preview', apply: 'serve',
    configureServer(server) {
      const connections = new Map<string, 'new' | 'ready' | 'connected'>()
      server.middlewares.use((request, response, next) => {
        const url = new URL(request.url ?? '/', 'http://localhost')
        const path = url.pathname
        if (path.startsWith('/hubs/')) {
          if (path === '/hubs/dashboard/negotiate' && request.method === 'POST') {
            const id = randomUUID()
            connections.set(id, 'new')
            json(response, { negotiateVersion: 1, connectionId: id, connectionToken: id,
              availableTransports: [{ transport: 'LongPolling', transferFormats: ['Text', 'Binary'] }] })
            return
          }
          const id = url.searchParams.get('id') ?? ''
          if (path !== '/hubs/dashboard' || !connections.has(id)) { response.writeHead(404); response.end(); return }
          if (request.method === 'DELETE') { connections.delete(id); response.end(); return }
          if (request.method === 'POST') { request.resume(); connections.set(id, 'ready'); response.end(); return }
          if (request.method !== 'GET') { response.writeHead(405); response.end(); return }
          // Use the real client's long-poll transport without contacting a SignalR service.
          setTimeout(() => {
            if (response.destroyed) return
            let body = ''
            if (connections.get(id) === 'ready') {
              connections.set(id, 'connected')
              body = '{}\u001e'
              const events = [
                ...dashboard.recentProducts.slice(0, 3).map((product, index) => ({ target: 'ReceiveProductUpdate', arguments: [{ ...product, id: 100 + index, barcode: `MOCK-RECENT-${index + 1}` }] })),
                { target: 'ReceiveLogUpdate', arguments: [{ id: 1, eventType: 'SYSTEM_START', message: 'Mock preview loaded. No devices are connected.', severity: 'INFO', createdAt: time() }] },
              ]
              body += events.map(event => JSON.stringify({ type: 1, ...event }) + '\u001e').join('')
            } else if (connections.get(id) === 'connected') {
              body = JSON.stringify({ type: 1, target: 'ReceiveEnvironmentUpdate', arguments: [{ temperature: 26.4, humidity: 46, fanActive: false }] }) + '\u001e'
            }
            response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' })
            response.end(body)
          }, 1000)
          return
        }
        if (!path.startsWith('/api/')) { next(); return }
        request.resume()
        if (request.method !== 'GET') {
          json(response, { isSuccess: false, code: 'MOCK_READ_ONLY', message: 'Mock preview is read-only. No operation was performed.', data: null }, 409)
          return
        }
        if (path === '/api/camera/preview' || /^\/api\/dataset\/sessions\/mock-session\/images\/mock-[0-2]\.svg$/.test(path)) {
          response.writeHead(200, { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'no-store' }); response.end(camera); return
        }
        const reads: Record<string, unknown> = {
          '/api/dashboard': dashboard, '/api/diagnostics': diagnostics, '/api/recipes': [recipe],
          '/api/calibration-profiles': [calibration], '/api/tracking/queue': queue, '/api/simulation/status': simulation,
          '/api/dataset/sessions': [dataset], '/api/dataset/sessions/active': dataset,
          '/api/control-room': { activeRecipe: recipe.name, currentProduct: queue[0], lastInspection: inspections[0],
            totalCount: 2480, acceptedCount: 2360, rejectedCount: 90, manualReviewCount: 30, yieldPercent: 96.3,
            binCounts: [{ destinationBin: 'BIN_A', count: 2360 }, { destinationBin: 'REJECT_BIN', count: 90 }, { destinationBin: 'MANUAL_REVIEW', count: 30 }],
            recentEvents: [{ createdAt: time(), eventType: 'MOCK_PREVIEW', message: 'Example production data loaded.', severity: 'INFO' }], simulationActive: false },
          '/api/statistics/hourly': Array.from({ length: 8 }, (_, index) => ({ ...statistics, id: index + 1, hour: index + 8,
            totalProducts: 180 + index * 30, defectiveProducts: 5 + index, redProducts: 70 + index * 10, blueProducts: 60 + index * 10, greenProducts: 50 + index * 10 })),
        }
        if (path === '/api/inspections') {
          const items = inspections.filter(item =>
            (!url.searchParams.get('decision') || item.decision === url.searchParams.get('decision')) &&
            (!url.searchParams.get('productClass') || item.productClass === url.searchParams.get('productClass')) &&
            (!url.searchParams.get('simulation') || String(item.simulation) === url.searchParams.get('simulation')) &&
            (!url.searchParams.get('q') || JSON.stringify(item).toLowerCase().includes(url.searchParams.get('q')!.toLowerCase())))
          const pageNumber = Math.max(1, Number(url.searchParams.get('pageNumber')) || 1)
          const pageSize = Math.min(100, Math.max(1, Number(url.searchParams.get('pageSize')) || 20))
          const start = (pageNumber - 1) * pageSize
          json(response, { isSuccess: true, data: { items: items.slice(start, start + pageSize), pageNumber, pageSize, hasNextPage: start + pageSize < items.length } })
          return
        }
        if (Object.hasOwn(reads, path)) { json(response, { isSuccess: true, data: reads[path] }); return }
        json(response, { isSuccess: false, code: 'RESOURCE_NOT_FOUND', message: 'No mock data exists for this route.', data: null }, 404)
      })
    },
  }
}
