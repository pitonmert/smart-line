export type Theme = 'dark' | 'light'
export const themeStorageKey = 'smartline.theme'

export function readTheme(read = () => localStorage.getItem(themeStorageKey)): Theme {
  try { return read() === 'light' ? 'light' : 'dark' } catch { return 'dark' }
}

export function saveTheme(theme: Theme, write = () => localStorage.setItem(themeStorageKey, theme)) {
  try { write() } catch { /* Theme switching remains available without storage. */ }
}
