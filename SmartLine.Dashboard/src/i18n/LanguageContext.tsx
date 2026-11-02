import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import type { MessageKey } from './catalog'
import { formatDate, formatMessage, formatNumber, localize, readLanguage, saveLanguage, translate, type DisplayText, type Language, type Message, type MessageParams } from './language'

function createLanguageTools(language: Language) {
  return {
    t: (key: MessageKey, params?: MessageParams) => translate(language, key, params),
    text: (value: DisplayText | null | undefined) => localize(language, value),
    number: (value: number, options?: Intl.NumberFormatOptions) => formatNumber(language, value, options),
    date: (value?: string | null) => formatDate(language, value),
    time: (value?: string | null) => formatDate(language, value, true),
    message: (value: Message | null) => formatMessage(language, value),
  }
}

type LanguageContextValue = ReturnType<typeof createLanguageTools> & { language: Language; setLanguage: (language: Language) => void }
const LanguageContext = createContext<LanguageContextValue | null>(null)

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [language, setLanguage] = useState<Language>(() => readLanguage())
  useEffect(() => {
    document.documentElement.lang = language
    document.title = translate(language, 'Dashboard title')
    saveLanguage(language)
  }, [language])
  const value = useMemo(() => ({ language, setLanguage, ...createLanguageTools(language) }), [language])
  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useLanguage() {
  const value = useContext(LanguageContext)
  if (!value) throw new Error('LanguageProvider is required.')
  return value
}
