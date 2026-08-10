// @refresh reset
import { createContext, useContext, useReducer, type Dispatch, type ReactNode } from 'react'
import type { ConveyorStatusDto, ProductDto, AlertDto, LogDto, StatisticsDto } from '../types'

export interface DashboardState {
  connection: 'connecting' | 'connected' | 'disconnected' | 'reconnecting'
  conveyor: ConveyorStatusDto | null
  todayStats: StatisticsDto | null
  recentProducts: ProductDto[]
  activeAlerts: AlertDto[]
  recentLogs: LogDto[]
  lastHeartbeat: Date | null
  chartMode: 'live' | 'historical'
  liveProductCounts: { time: string; ok: number; defective: number }[]
  error: string | null
}

export type DashboardAction =
  | { type: 'SET_INITIAL_DATA'; conveyor: ConveyorStatusDto; stats: StatisticsDto | null; products: ProductDto[]; alerts: AlertDto[] }
  | { type: 'UPDATE_CONVEYOR'; payload: ConveyorStatusDto }
  | { type: 'UPDATE_ENVIRONMENT'; temperature: number; humidity: number; fanActive: boolean }
  | { type: 'ADD_PRODUCT'; payload: ProductDto }
  | { type: 'UPDATE_ALERT'; payload: AlertDto }
  | { type: 'ADD_LOG'; payload: LogDto }
  | { type: 'SET_CONNECTION'; payload: DashboardState['connection'] }
  | { type: 'SET_CHART_MODE'; payload: 'live' | 'historical' }
  | { type: 'SET_ERROR'; payload: string }

const initialState: DashboardState = {
  connection: 'connecting',
  conveyor: null,
  todayStats: null,
  recentProducts: [],
  activeAlerts: [],
  recentLogs: [],
  lastHeartbeat: null,
  chartMode: 'live',
  liveProductCounts: [],
  error: null,
}

function reducer(state: DashboardState, action: DashboardAction): DashboardState {
  switch (action.type) {
    case 'SET_INITIAL_DATA':
      return {
        ...state,
        error: null,
        conveyor: action.conveyor,
        todayStats: action.stats,
        recentProducts: action.products,
        activeAlerts: action.alerts,
      }
    case 'UPDATE_CONVEYOR':
      return { ...state, conveyor: action.payload }
    case 'UPDATE_ENVIRONMENT':
      return {
        ...state,
        conveyor: state.conveyor
          ? { ...state.conveyor, temperature: action.temperature, humidity: action.humidity, fanActive: action.fanActive }
          : state.conveyor,
        lastHeartbeat: new Date(),
      }
    case 'ADD_PRODUCT': {
      const now = new Date()
      const timeKey = `${now.getHours()}:${String(now.getMinutes()).padStart(2, '0')}`
      const isDefective = action.payload.status !== 'OK'
      const counts = [...state.liveProductCounts]
      const last = counts[counts.length - 1]
      if (last?.time === timeKey) {
        counts[counts.length - 1] = {
          ...last,
          ok: last.ok + (isDefective ? 0 : 1),
          defective: last.defective + (isDefective ? 1 : 0),
        }
      } else {
        counts.push({ time: timeKey, ok: isDefective ? 0 : 1, defective: isDefective ? 1 : 0 })
        if (counts.length > 10) counts.shift()
      }
      return {
        ...state,
        recentProducts: [action.payload, ...state.recentProducts].slice(0, 5),
        liveProductCounts: counts,
      }
    }
    case 'UPDATE_ALERT':
      if (action.payload.isActive) {
        const exists = state.activeAlerts.some(a => a.id === action.payload.id)
        return {
          ...state,
          activeAlerts: exists
            ? state.activeAlerts.map(a => a.id === action.payload.id ? action.payload : a)
            : [action.payload, ...state.activeAlerts],
        }
      }
      return { ...state, activeAlerts: state.activeAlerts.filter(a => a.id !== action.payload.id) }
    case 'ADD_LOG':
      if (action.payload.eventType === 'PRODUCT_PASSED') return state
      return { ...state, recentLogs: [action.payload, ...state.recentLogs].slice(0, 10) }
    case 'SET_CONNECTION':
      return { ...state, connection: action.payload }
    case 'SET_CHART_MODE':
      return { ...state, chartMode: action.payload }
    case 'SET_ERROR':
      return { ...state, error: action.payload }
    default:
      return state
  }
}

const Ctx = createContext<DashboardState>(initialState)
const DispatchCtx = createContext<Dispatch<DashboardAction>>(() => {})

export function DashboardProvider({ children }: { children: ReactNode }) {
  const [state, dispatch] = useReducer(reducer, initialState)
  return (
    <Ctx.Provider value={state}>
      <DispatchCtx.Provider value={dispatch}>{children}</DispatchCtx.Provider>
    </Ctx.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export const useDashboardState = () => useContext(Ctx)
// eslint-disable-next-line react-refresh/only-export-components
export const useDashboardDispatch = () => useContext(DispatchCtx)
