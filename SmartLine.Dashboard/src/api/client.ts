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
      response.ok ? 'API geçersiz bir yanıt döndürdü.' : response.statusText || 'İstek başarısız oldu.',
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
      envelope.message || response.statusText || 'İstek başarısız oldu.',
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
  REQUEST_INVALID: 'Gönderilen bilgiler geçersiz.',
  RESOURCE_NOT_FOUND: 'İstenen kayıt bulunamadı.',
  PRODUCT_BARCODE_DUPLICATE: 'Bu barkod daha önce kullanılmış.',
  PRODUCT_COLOR_INVALID: 'Ürün rengi geçerli değil.',
  PRODUCT_STATUS_INVALID: 'Ürün durumu geçerli değil.',
  CONVEYOR_MUST_BE_STOPPED: 'Hız değişikliği için konveyör durdurulmalıdır.',
  CONVEYOR_EMERGENCY_LATCHED: 'Acil durdurma kilidi açılmadan konveyör başlatılamaz.',
  FIRE_SENSOR_ACTIVE: 'Yangın sensörü aktifken acil durum kilidi açılamaz.',
  FIRE_GENERATION_MISMATCH: 'Yangın sensörü güvenlik sayacı geçersiz. Cihaz yeniden provision edilmeden kilit açılamaz.',
  MQTT_UNAVAILABLE: 'Cihaz bağlantısı kullanılamıyor. İşlem uygulanamadı.',
  DATABASE_CONFLICT: 'İşlem başka bir güncellemeyle çakıştı. Lütfen yeniden deneyin.',
  UNEXPECTED_ERROR: 'Beklenmeyen bir hata oluştu.',
}

export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError) return errorMessages[error.code] ?? error.message ?? fallback
  return error instanceof Error ? error.message : fallback
}
