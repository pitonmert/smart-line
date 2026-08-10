import { useEffect } from 'react'
import { useDashboardDispatch } from '../context/DashboardContext'
import { api, getApiErrorMessage } from '../api/client'

export function useDashboard() {
  const dispatch = useDashboardDispatch()

  useEffect(() => {
    api.getDashboard()
      .then(data => {
        dispatch({
          type: 'SET_INITIAL_DATA',
          conveyor: data.conveyorStatus,
          stats: data.todayStatistics,
          products: data.recentProducts,
          alerts: data.activeAlerts,
        })
      })
      .catch((err) => {
        console.error('[Dashboard] Veri yüklenemedi:', err)
        dispatch({
          type: 'SET_ERROR',
          payload: getApiErrorMessage(err, 'Dashboard verileri yüklenemedi.'),
        })
      })
  }, [dispatch])
}
