export const routes = [
  { path: '/', label: 'Control Room' },
  { path: '/inspection', label: 'AI Inspection' },
  { path: '/analytics', label: 'Production Analytics' },
  { path: '/sensors', label: 'Sensors' },
  { path: '/sorting', label: 'Sorting' },
  { path: '/recipes', label: 'Product Recipes' },
  { path: '/calibration', label: 'Calibration' },
  { path: '/dataset', label: 'Dataset & AI' },
  { path: '/history', label: 'Inspection History' },
  { path: '/diagnostics', label: 'System Diagnostics' },
] as const

export type AppPath = (typeof routes)[number]['path']
