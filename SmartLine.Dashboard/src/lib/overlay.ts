export interface SourceBox {
  x: number
  y: number
  width: number
  height: number
}

export interface ScreenBox extends SourceBox {
  visible: boolean
}

/**
 * Map inspection pixels onto a CSS object-fit: contain camera view.
 *
 * The MJPEG image keeps its real pixel size. The browser letterboxes it inside
 * the panel. Overlay coordinates use the same scale and the same leftover
 * margin, so a wider panel does not pretend the camera resolution increased.
 * sourceWidth and sourceHeight must be the frame the boxes were measured on.
 */
export function mapSourceBox(
  box: SourceBox,
  sourceWidth: number,
  sourceHeight: number,
  containerWidth: number,
  containerHeight: number,
): ScreenBox {
  if (sourceWidth <= 0 || sourceHeight <= 0 || containerWidth <= 0 || containerHeight <= 0) {
    return { x: 0, y: 0, width: 0, height: 0, visible: false }
  }

  const scale = Math.min(containerWidth / sourceWidth, containerHeight / sourceHeight)
  const offsetX = (containerWidth - sourceWidth * scale) / 2
  const offsetY = (containerHeight - sourceHeight * scale) / 2
  return {
    x: offsetX + box.x * scale,
    y: offsetY + box.y * scale,
    width: box.width * scale,
    height: box.height * scale,
    visible: true,
  }
}

export function normalizedRoiToSource(
  roi: { x: number; y: number; width: number; height: number },
  sourceWidth: number,
  sourceHeight: number,
): SourceBox {
  return {
    x: roi.x * sourceWidth,
    y: roi.y * sourceHeight,
    width: roi.width * sourceWidth,
    height: roi.height * sourceHeight,
  }
}
