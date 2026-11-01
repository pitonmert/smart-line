export interface ConveyorStatusDto {
  speed: number
  isRunning: boolean
  isEmergencyStopped: boolean
  isFireDetected: boolean
  fireDeviceEpoch: string
  lastFireGeneration: number
  isFireGenerationFault: boolean
  temperature: number
  humidity: number
  fanActive: boolean
  lastUpdated: string
}

export interface ProductDto {
  id: number
  barcode: string
  color: string | null
  status: string
  passedAt: string
}

export interface AlertDto {
  id: number
  alertType: string
  isActive: boolean
  triggeredAt: string
  resolvedAt: string | null
}

export interface LogDto {
  id: number
  eventType: string
  message: string | null
  severity: string
  createdAt: string
}

export interface StatisticsDto {
  id: number
  date: string
  hour: number | null
  totalProducts: number
  defectiveProducts: number
  redProducts: number
  blueProducts: number
  greenProducts: number
  yellowProducts: number
  defectiveRate: number
}

export interface DashboardDto {
  conveyorStatus: ConveyorStatusDto
  todayStatistics: StatisticsDto | null
  recentProducts: ProductDto[]
  activeAlerts: AlertDto[]
}

export interface ApiResponse<T> {
  isSuccess: boolean
  message: string
  code?: string | null
  data: T
}
