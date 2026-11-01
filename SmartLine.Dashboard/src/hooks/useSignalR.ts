import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useDashboardDispatch } from '../context/DashboardContext'
import { api } from '../api/client'
import type { ConveyorStatusDto, ProductDto, AlertDto, LogDto } from '../types'

export function useSignalR() {
  const dispatch = useDashboardDispatch()
  const connRef = useRef<ReturnType<typeof HubConnectionBuilder.prototype.build> | null>(null)

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/dashboard')
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()

    connRef.current = connection

    connection.onreconnecting(() => dispatch({ type: 'SET_CONNECTION', payload: 'reconnecting' }))
    connection.onreconnected(async () => {
      dispatch({ type: 'SET_CONNECTION', payload: 'connected' })
      try {
        const data = await api.getDashboard()
        dispatch({
          type: 'SET_INITIAL_DATA',
          conveyor: data.conveyorStatus,
          stats: data.todayStatistics,
          products: data.recentProducts,
          alerts: data.activeAlerts,
        })
      } catch { /* stale data temizligi basarisiz — ihmal et */ }
    })
    connection.onclose(() => dispatch({ type: 'SET_CONNECTION', payload: 'disconnected' }))

    connection.on('ReceiveConveyorUpdate', (status: ConveyorStatusDto) =>
      dispatch({ type: 'UPDATE_CONVEYOR', payload: status }))

    connection.on('ReceiveEnvironmentUpdate', (data: { temperature: number; humidity: number; fanActive: boolean }) =>
      dispatch({ type: 'UPDATE_ENVIRONMENT', temperature: data.temperature, humidity: data.humidity, fanActive: data.fanActive }))

    connection.on('ReceiveProductUpdate', (product: ProductDto) =>
      dispatch({ type: 'ADD_PRODUCT', payload: product }))

    connection.on('ReceiveAlertUpdate', (alert: AlertDto) =>
      dispatch({ type: 'UPDATE_ALERT', payload: alert }))

    connection.on('ReceiveLogUpdate', (log: LogDto) =>
      dispatch({ type: 'ADD_LOG', payload: log }))

    connection.on('ReceiveInspectionUpdate', (inspection: unknown) =>
      window.dispatchEvent(new CustomEvent('smartline-inspection', { detail: inspection })))

    connection.on('ReceiveTrackingUpdate', (track: unknown) =>
      window.dispatchEvent(new CustomEvent('smartline-tracking', { detail: track })))

    connection.start()
      .then(() => dispatch({ type: 'SET_CONNECTION', payload: 'connected' }))
      .catch((err) => {
        console.error('[SignalR] Bağlantı hatası:', err)
        dispatch({ type: 'SET_CONNECTION', payload: 'disconnected' })
      })

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        connection.stop()
      }
    }
  }, [dispatch])
}
