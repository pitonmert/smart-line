import { useApiAvailability, usePlant } from '../context/PlantContext'
import type { DisplayText } from '../i18n/language'
import { useLanguage } from '../i18n/LanguageContext'
import { useState } from 'react'
import { api, getApiErrorMessage } from '../api/client'
import { ErrorNote, Panel } from '../components/inspection/ui'
import type { RecipeSummary } from '../types/inspection'

export default function RecipesPage() {
  const apiAvailable = useApiAvailability()
  const { recipes } = usePlant()
  const { number, t, text } = useLanguage()
  const [selected, setSelected] = useState<string | null>(null)
  const [error, setError] = useState<DisplayText | null>(null)
  const list = recipes.data ?? []
  const recipe = list.find(item => item.id === (selected ?? list.find(entry => entry.isActive)?.id)) ?? list[0]

  const activate = async (item: RecipeSummary) => {
    if (!apiAvailable) return
    setError(null)
    try {
      await api.activateRecipe(item.id)
      recipes.reload()
    } catch (reason) {
      setError(getApiErrorMessage(reason, 'Recipe was not activated.'))
    }
  }

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[240px_minmax(0,1fr)]">
      <Panel title={t("Recipes")}>
        <ul className="space-y-2">
          {(recipes.data ?? []).map(item => (
            <li key={item.id}>
              <button type="button" className="w-full rounded-lg px-2 py-1 text-left text-sm hover:bg-card-border/30" onClick={() => setSelected(item.id)}>
                {item.name} {item.isActive && <span className="text-success-light">{t("active")}</span>}
              </button>
            </li>
          ))}
        </ul>
      </Panel>
      {recipe && (
        <div className="flex flex-col gap-4">
          <Panel title={recipe.name} action={!recipe.isActive ? <button type="button" className="rounded bg-primary px-3 py-1 text-xs text-white" disabled={!apiAvailable} onClick={() => activate(recipe)}>{t("Activate")}</button> : undefined}>
            <p className="text-sm text-text-muted">{recipe.description}</p>
            <p className="mt-2 text-xs">{t("Version")}{' '}{number(recipe.version)}{t(". DemoBox is the first pipeline. Another recipe can replace it without changing the decision engine.")}</p>
            {error && <div className="mt-3"><ErrorNote>{error}</ErrorNote></div>}
          </Panel>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Panel title={t("Classes")}><ul className="text-sm">{recipe.definition.productClasses.map(item => <li key={item.code}>{item.code} · {item.expectedColor}</li>)}</ul></Panel>
            <Panel title={t("Colors")}><ul className="text-sm">{recipe.definition.allowedColors.map(item => <li key={item.code}>{item.code}{' '}{t("hue")}{' '}{number(item.hueMin)}–{number(item.hueMax)}</li>)}</ul></Panel>
            <Panel title={t("Sizes")}><ul className="text-sm">{recipe.definition.sizeClasses.map(item => <li key={item.code}>{item.code} {number(item.minWidthMm)}–{number(item.maxWidthMm)} mm</li>)}</ul></Panel>
            <Panel title={t("Defects")}><ul className="text-sm">{recipe.definition.defectClasses.map(item => <li key={item.code}>{item.code} {item.rejects ? t('Rejects') : t('Holds')}</li>)}</ul></Panel>
          </div>
          <Panel title={t("Sorting rules")}>
            <ul className="space-y-1 text-sm">
              {recipe.definition.sortingRules.map(rule => (
                <li key={rule.id}>{number(rule.priority)}. {rule.id} → {rule.destinationBin}{' '}{t("when")}{' '}{[rule.when.quality, rule.when.productClass, rule.when.colorClass, rule.when.sizeClass, rule.when.defectClass].filter(Boolean).map(value => text(value)).join(' + ') || t('Any')}</li>
              ))}
            </ul>
          </Panel>
          <Panel title={t("Model and camera")}>
            <p className="text-sm">{recipe.definition.model.family} {recipe.definition.model.name} · {recipe.definition.model.task}{' '}{t("· version")}{' '}{recipe.definition.model.version}</p>
            <p className="mt-1 text-sm text-text-muted">{t("Artifact:")}{' '}{recipe.definition.model.artifactFileName ?? t('Not registered')}{t(". Continuous inference:")}{' '}{recipe.definition.inspection.continuousInference ? t('On') : t('Off')}.</p>
            <p className="mt-1 text-sm text-text-muted">{recipe.definition.cameraProfile.sensor} {recipe.definition.cameraProfile.pixelFormat} {recipe.definition.cameraProfile.transport}{' '}{t("· live")}{' '}{recipe.definition.cameraProfile.sourceWidth}×{recipe.definition.cameraProfile.sourceHeight}</p>
          </Panel>
        </div>
      )}
    </div>
  )
}
