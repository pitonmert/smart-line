import type { MessageKey } from '../i18n/catalog'

export const mainRoutes = [
  { path: '/', label: 'Production' },
  { path: '/history', label: 'History' },
  { path: '/management', label: 'Management' },
] as const satisfies readonly { path: string; label: MessageKey }[]

export const managementGroups = [
  { label: 'Line control', description: 'Operate the conveyor and manage its stop latch.', links: [
    { path: '/management/control', label: 'Conveyor controls' },
  ] },
  { label: 'Settings & maintenance', description: 'Configure recipes and calibration; inspect device health.', links: [
    { path: '/management/recipes', label: 'Product Recipes' },
    { path: '/management/calibration', label: 'Calibration' },
    { path: '/management/sensors', label: 'Sensors' },
    { path: '/management/diagnostics', label: 'System Diagnostics' },
  ] },
  { label: 'Tools', description: 'Manual checks, dataset collection and simulation, outside the production screen.', links: [
    { path: '/management/inspection', label: 'Manual inspection' },
    { path: '/management/sorting', label: 'Logical sorting' },
    { path: '/management/dataset', label: 'Dataset collection' },
    { path: '/management/simulation', label: 'Simulation tools' },
  ] },
] as const satisfies readonly { label: MessageKey; description: MessageKey; links: readonly { path: string; label: MessageKey }[] }[]

export type AppPath = typeof mainRoutes[number]['path'] | typeof managementGroups[number]['links'][number]['path']
export const routes = [...mainRoutes, ...managementGroups.flatMap(group => [...group.links])]
const aliases: Record<string, AppPath> = {
  '/inspection': '/management/inspection', '/analytics': '/history', '/sensors': '/management/sensors',
  '/sorting': '/management/sorting', '/recipes': '/management/recipes', '/calibration': '/management/calibration',
  '/dataset': '/management/dataset', '/diagnostics': '/management/diagnostics',
}

export function resolvePath(path: string): AppPath | null {
  if (Object.hasOwn(aliases, path)) return aliases[path]
  return routes.find(route => route.path === path)?.path ?? null
}
