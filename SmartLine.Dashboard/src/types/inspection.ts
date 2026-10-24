export interface BoundingBox {
  x: number
  y: number
  width: number
  height: number
  label?: string | null
  confidence?: number | null
  sourceWidth?: number | null
  sourceHeight?: number | null
}

export interface InspectionResult {
  inspectionId: string
  productId: string
  recipeId: string
  capturedAt: string
  processedAt: string
  productClass: string
  colorClass: string
  sizeClass: string
  defectClasses: string[]
  qualityStatus: string
  confidence: number
  boundingBoxes: BoundingBox[]
  geometry?: { sourceWidth?: number | null; sourceHeight?: number | null; roi?: NormalizedRoi | null } | null
  visualWidth?: number | null
  visualHeight?: number | null
  estimatedPhysicalDimensions?: {
    widthMm?: number | null
    heightMm?: number | null
    method?: string | null
    depthAvailable?: boolean
    distanceMm?: number | null
  } | null
  modelName?: string | null
  modelVersion?: string | null
  decision: string
  destinationBin: string
  processingLatencyMs?: number | null
  cameraSource?: string | null
  imageReference?: string | null
  simulation: boolean
  ruleId?: string | null
  reasons?: string[]
  recipeVersion?: number | null
  frameRole?: string | null
  captureSource?: string | null
  previewFallback?: boolean | null
}

export interface ProductTrack {
  productId: string
  publicCode: string
  recipeId?: string | null
  inspectionId?: string | null
  stage: string
  simulation: boolean
  destinationBin?: string | null
  decision?: string | null
  failureReason?: string | null
  detectedAt: string
  capturedAt?: string | null
  inspectingAt?: string | null
  decidedAt?: string | null
  queuedAt?: string | null
  sortedAt?: string | null
  failedAt?: string | null
  manualReviewAt?: string | null
  sortDueAt?: string | null
}

export interface BinCount {
  destinationBin: string
  count: number
}

export interface ControlRoomEvent {
  createdAt: string
  eventType: string
  message?: string | null
  severity: string
}

export interface ControlRoom {
  activeRecipe?: string | null
  currentProduct?: ProductTrack | null
  lastInspection?: InspectionResult | null
  totalCount: number
  acceptedCount: number
  rejectedCount: number
  manualReviewCount: number
  yieldPercent?: number | null
  binCounts: BinCount[]
  recentEvents: ControlRoomEvent[]
  simulationActive: boolean
}

export interface SimulationStatus {
  enabled: boolean
  active: boolean
  latched: boolean
  inFlightSimulatedProducts: number
}

export interface DiagnosticsReport {
  api: string
  database: string
  mqtt: string
  vision: string
  camera: string
  lastInspectionAt?: string | null
  modelStatus: string
  modelName?: string | null
  modelVersion?: string | null
  esp32: string
  signalR: string
  simulation: SimulationStatus
  hardwareActuationEnabled: boolean
  environment: string
  activeRecipe?: string | null
  timeOfFlightSource: string
  cameraStreamUrl?: string | null
  lastFrameAt?: string | null
  frameWidth?: number | null
  frameHeight?: number | null
  cameraConsecutiveFailures?: number | null
  cameraStreamHost?: string | null
  model?: ModelDiagnostics | null
  stillConfigured?: boolean | null
  configuredStillWidth?: number | null
  configuredStillHeight?: number | null
  lastStillWidth?: number | null
  lastStillHeight?: number | null
  lastStillError?: string | null
  previewFallbackAllowed?: boolean | null
  sensorPid?: string | null
  activeRecipeVersion?: number | null
}

export interface ModelDiagnostics {
  status: string
  name?: string | null
  version?: string | null
  configured: boolean
  loaded: boolean
  backend?: string | null
  task?: string | null
  artifact?: string | null
  lastInferenceLatencyMs?: number | null
  loadError?: string | null
  confidenceThreshold?: number | null
}

export interface NormalizedRoi {
  x: number
  y: number
  width: number
  height: number
}

export interface RecipeDefinition {
  name: string
  description?: string
  productClasses: { code: string; displayName: string; expectedColor?: string | null }[]
  allowedColors: { code: string; displayName: string; hueMin: number; hueMax: number }[]
  sizeClasses: { code: string; displayName: string; minWidthMm: number; maxWidthMm: number }[]
  defectClasses: { code: string; displayName: string; rejects: boolean }[]
  acceptance: { minimumConfidence: number; defaultDestinationBin: string }
  sortingRules: { id: string; priority: number; destinationBin: string; when: { quality?: string | null; productClass?: string | null; colorClass?: string | null; sizeClass?: string | null; defectClass?: string | null } }[]
  destinationBins: { code: string; displayName: string; role: string }[]
  model: { task: string; family: string; name: string; version: string; artifactFileName?: string | null; inputSize: number }
  cameraProfile: { streamUrl: string; sourceWidth: number; sourceHeight: number; sensor: string; pixelFormat: string; transport: string; notes?: string }
  roi: NormalizedRoi
  inspection: { continuousInference: boolean; triggerModes: string[]; selectionStrategy: string }
}

export interface RecipeSummary {
  id: string
  name: string
  description: string
  isActive: boolean
  version: number
  definition: RecipeDefinition
  updatedAt: string
}

export interface CalibrationDefinition {
  sourceWidth?: number | null
  sourceHeight?: number | null
  cameraRoi?: { x: number; y: number; width: number; height: number } | null
  inspectionZone?: { x: number; y: number; width: number; height: number } | null
  perspectiveReference?: { x: number; y: number }[] | null
  pixelsPerMillimeter?: number | null
  orientationDegrees: number
  flipHorizontal: boolean
  flipVertical: boolean
  colorReference?: { red: number; green: number; blue: number; illuminantNote?: string | null } | null
  beltSpeedMmPerSecond?: number | null
  inspectionToSorterDistanceMm?: number | null
  gateMappings: { binCode: string; servoDegrees: number; enabled: boolean }[]
  tofBaseline: {
    source: string
    productPresent?: boolean | null
    distanceMm?: number | null
    triggerTimestamp?: string | null
    sensorHealthy?: boolean | null
  }
  hardwareActuationEnabled: boolean
  hardwareActuationConfirmed: boolean
  notes?: string | null
}

export interface CalibrationProfile {
  id: string
  name: string
  description: string
  isActive: boolean
  version: number
  definition: CalibrationDefinition
  simulationDataPresent: boolean
  updatedAt: string
}

export interface DatasetSession {
  sessionId: string
  recipeId?: string | null
  recipeName: string
  status: string
  productClass: string
  qualityLabel: string
  colorLabel: string
  sizeLabel: string
  defectLabels: string[]
  notes?: string | null
  captureMode: string
  intervalMs?: number | null
  imageCount: number
  classCounts: Record<string, number>
  startedAt: string
  stoppedAt?: string | null
  simulation: boolean
  labelStatus: string
  qualityCounts: Record<string, number>
  defectCounts: Record<string, number>
  recentSamples: DatasetSample[]
}

export interface DatasetSample {
  sampleId: string
  fileName: string
  capturedAt: string
  productClass: string
  qualityLabel: string
  colorLabel: string
  sizeLabel: string
  defectLabels: string[]
  notes?: string | null
  trigger: string
  frameRole: string
  sourceWidth?: number | null
  sourceHeight?: number | null
  captureSource?: string | null
  previewFallback?: boolean | null
}

export interface DatasetCaptureResult {
  session: DatasetSession
  sample: DatasetSample
}

export interface Paged<T> {
  items: T[]
  hasNextPage: boolean
  pageNumber: number
  pageSize: number
}

export interface CompletedInspection {
  inspection: InspectionResult
  track: ProductTrack
  actuation: { hardwareCommandIssued: boolean; actuation: string; message: string }
}

export interface ReleaseResult {
  track: ProductTrack
  actuation: { hardwareCommandIssued: boolean; actuation: string; message: string }
}
