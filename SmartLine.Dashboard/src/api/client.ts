import type { DisplayText } from '../i18n/language'
import type {
  AlertDto,
  ApiResponse,
  ConveyorStatusDto,
  DashboardDto,
  StatisticsDto,
} from '../types'
import type {
  CalibrationDefinition,
  CalibrationProfile,
  CompletedInspection,
  ControlRoom,
  DatasetCaptureResult,
  DatasetSession,
  DiagnosticsReport,
  InspectionResult,
  Paged,
  ProductTrack,
  RecipeSummary,
  ReleaseResult,
  SimulationStatus,
} from '../types/inspection'

const BASE = '/api'
export class ApiError extends Error {
  public readonly code: string
  public readonly status: number

  constructor(
    message: string,
    code: string,
    status: number,
  ) {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
  }
}

async function parseResponse<T>(response: Response): Promise<ApiResponse<T>> {
  const fallbackCode = response.ok ? 'UNEXPECTED_ERROR' : `HTTP_${response.status}`

  try {
    return await response.json() as ApiResponse<T>
  } catch {
    throw new ApiError(
      response.ok ? 'The API returned an invalid response.' : response.statusText || 'Request failed.',
      fallbackCode,
      response.status,
    )
  }
}

async function send<T>(
  method: string,
  path: string,
  body?: unknown,
): Promise<{ response: Response; envelope: ApiResponse<T> }> {
  const headers = new Headers()
  if (body !== undefined) headers.set('Content-Type', 'application/json')

  const response = await fetch(`${BASE}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  return { response, envelope: await parseResponse<T>(response) }
}

function unwrap<T>(response: Response, envelope: ApiResponse<T>): T {
  if (!response.ok || !envelope.isSuccess) {
    throw new ApiError(
      envelope.message || response.statusText || 'Request failed.',
      envelope.code || `HTTP_${response.status}`,
      response.status,
    )
  }

  return envelope.data
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const result = await send<T>(method, path, body)
  return unwrap(result.response, result.envelope)
}

export const api = {
  getDashboard: () => request<DashboardDto>('GET', '/dashboard'),

  getHourlyStats: (date?: string) =>
    request<StatisticsDto[]>('GET', `/statistics/hourly${date ? `?date=${date}` : ''}`),

  updateSpeed: (speed: number) =>
    request<ConveyorStatusDto>('POST', '/conveyor/speed', { speed }),

  startConveyor: () =>
    request<ConveyorStatusDto>('POST', '/conveyor/start'),

  stopConveyor: () =>
    request<ConveyorStatusDto>('POST', '/conveyor/stop'),

  emergencyStopConveyor: () =>
    request<ConveyorStatusDto>('POST', '/conveyor/emergency-stop'),

  resetEmergencyStop: () =>
    request<ConveyorStatusDto>('POST', '/conveyor/emergency-reset'),

  resolveAlert: (id: number) =>
    request<AlertDto>('POST', `/alerts/${id}/resolve`),

  sendEnvironmentData: (temperature: number, humidity: number) =>
    request<string>('POST', '/sensors/environment', { temperature, humidity }),

  sendFireAlert: (isDetected: boolean, deviceEpoch: string, fireGeneration: number) =>
    request<string>('POST', '/sensors/fire', { isDetected, deviceEpoch, fireGeneration }),

  sendBarcodeData: (barcode: string, color?: string) =>
    request<string>('POST', '/sensors/barcode', { barcode, color }),

  getControlRoom: () => request<ControlRoom>('GET', '/control-room'),
  getDiagnostics: () => request<DiagnosticsReport>('GET', '/diagnostics'),
  getRecipes: () => request<RecipeSummary[]>('GET', '/recipes'),
  activateRecipe: (id: string) => request<RecipeSummary>('POST', `/recipes/${id}/activate`),
  getCalibrationProfiles: () => request<CalibrationProfile[]>('GET', '/calibration-profiles'),
  createCalibrationProfile: (body: { name: string; description: string; definition: CalibrationDefinition }) =>
    request<CalibrationProfile>('POST', '/calibration-profiles', body),
  activateCalibrationProfile: (id: string) => request<CalibrationProfile>('POST', `/calibration-profiles/${id}/activate`),
  getInspections: (query: string) => request<Paged<InspectionResult>>('GET', `/inspections${query}`),
  inspectVision: (requestId: string) => request<CompletedInspection>('POST', '/inspections/vision', { requestId }),
  async readCameraPreview() {
    const response = await fetch(`${BASE}/camera/preview`)
    if (!response.ok) throw new ApiError('Camera preview unavailable.', 'VISION_UNAVAILABLE', response.status)
    return response.blob()
  },
  submitInspection: (body: Record<string, unknown>) => request<CompletedInspection>('POST', '/inspections', body),
  getSortQueue: () => request<ProductTrack[]>('GET', '/tracking/queue'),
  releaseTrack: (id: string) => request<ReleaseResult>('POST', `/tracking/${id}/release`),
  getSimulationStatus: () => request<SimulationStatus>('GET', '/simulation/status'),
  simulateArrival: (body: Record<string, unknown>) => request<CompletedInspection>('POST', '/simulation/arrivals', body),
  tickSimulation: (utcNow: string) => request<{ sortedCount: number; actuation: ReleaseResult['actuation'] }>('POST', '/simulation/tick', { utcNow }),
  getDatasetSessions: () => request<DatasetSession[]>('GET', '/dataset/sessions'),
  startDatasetSession: (body: Record<string, unknown>) => request<DatasetSession>('POST', '/dataset/sessions', body),
  stopDatasetSession: (id: string) => request<DatasetSession>('POST', `/dataset/sessions/${id}/stop`),
  captureDatasetFromCamera: (id: string, body: {
    productClass: string
    qualityLabel: string
    colorLabel: string
    sizeLabel: string
    defectLabels: string[]
    notes: string
  }) => request<DatasetCaptureResult>('POST', `/dataset/sessions/${id}/captures/camera`, body),
  rejectDatasetSample: (id: string, sampleId: string) => request<DatasetSession>('DELETE', `/dataset/sessions/${id}/samples/${sampleId}`),

  async readDatasetImage(sessionId: string, fileName: string) {
    const response = await fetch(`${BASE}/dataset/sessions/${sessionId}/images/${encodeURIComponent(fileName)}`)
    if (!response.ok) {
      throw new ApiError('Dataset image was not found.', 'RESOURCE_NOT_FOUND', response.status)
    }
    return response.blob()
  },
}

const errorMessages: Record<string, string> = {
  RECIPE_INVALID: 'The recipe is invalid or incompatible with the model.',
  CALIBRATION_INVALID: 'The calibration is invalid for this inspection.',
  SIMULATION_DISABLED: 'Simulation is disabled.',
  TRACKING_TRANSITION_INVALID: 'This product state transition is not allowed.',
  DATASET_INVALID: 'The dataset operation could not be completed.',
  VISION_UNAVAILABLE: 'Vision or camera is unavailable.',
  VISION_TIMEOUT: 'The Vision request timed out.',
  VISION_INVALID: 'Vision returned an invalid result.',
  MOCK_READ_ONLY: 'Mock preview is read-only. No operation was performed.',
  REQUEST_INVALID: 'Invalid request data.',
  RESOURCE_NOT_FOUND: 'The requested record was not found.',
  PRODUCT_BARCODE_DUPLICATE: 'This barcode has already been used.',
  PRODUCT_COLOR_INVALID: 'Invalid product color.',
  PRODUCT_STATUS_INVALID: 'Invalid product status.',
  CONVEYOR_MUST_BE_STOPPED: 'Stop the conveyor before changing its speed.',
  CONVEYOR_EMERGENCY_LATCHED: 'Reset the emergency-stop latch before starting the conveyor.',
  FIRE_SENSOR_ACTIVE: 'The fire sensor is active; the latch cannot be reset.',
  FIRE_GENERATION_MISMATCH: 'Invalid fire safety counter. Reprovision the device before resetting the latch.',
  MQTT_UNAVAILABLE: 'The device connection is unavailable. The operation was not performed.',
  DATABASE_CONFLICT: 'The record changed during this operation. Please try again.',
  UNEXPECTED_ERROR: 'An unexpected error occurred.',
}

export function getApiErrorMessage(error: unknown, fallback: string): DisplayText {
  if (error instanceof ApiError)
    return Object.hasOwn(errorMessages, error.code) ? errorMessages[error.code] : { raw: error.message || fallback }
  return error instanceof Error ? { raw: error.message } : fallback
}
