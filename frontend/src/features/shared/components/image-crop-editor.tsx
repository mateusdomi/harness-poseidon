import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Move, RotateCcw, ZoomIn } from 'lucide-react';

import { Button, Input } from '@/design-system';
import {
  calculateCropLayout,
  cropImageFile,
  readImageFile,
  type CropTransform,
  type ImageDimensions,
} from '@/features/shared/lib/image-crop';

const PREVIEW_SIZE = 280;

export function ImageCropEditor({
  file,
  subjectName,
  pending = false,
  error = null,
  onCancel,
  onConfirm,
}: {
  file: File;
  subjectName: string;
  pending?: boolean;
  error?: string | null;
  onCancel: () => void;
  onConfirm: (file: File) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [source, setSource] = useState('');
  const [readFailed, setReadFailed] = useState(false);
  const [dimensions, setDimensions] = useState<ImageDimensions | null>(null);
  const [transform, setTransform] = useState<CropTransform>({ zoom: 1.15, x: 0, y: 15 });
  const [processing, setProcessing] = useState(false);
  const [processingError, setProcessingError] = useState<string | null>(null);
  const drag = useRef<{
    pointerId: number;
    x: number;
    y: number;
    startX: number;
    startY: number;
  } | null>(null);
  const layout = dimensions ? calculateCropLayout(dimensions, PREVIEW_SIZE, transform) : null;

  useEffect(() => {
    let active = true;
    setSource('');
    setDimensions(null);
    setReadFailed(false);
    void readImageFile(file)
      .then((value) => {
        if (active) setSource(value);
      })
      .catch(() => {
        if (active) setReadFailed(true);
      });
    return () => {
      active = false;
    };
  }, [file]);

  function movePointer(event: ReactPointerEvent<HTMLDivElement>) {
    if (!drag.current || !layout || event.pointerId !== drag.current.pointerId) return;
    const maxX = Math.max(1, (layout.width - PREVIEW_SIZE) / 2);
    const maxY = Math.max(1, (layout.height - PREVIEW_SIZE) / 2);
    setTransform((current) => ({
      ...current,
      x: Math.max(
        -100,
        Math.min(100, drag.current!.startX + ((event.clientX - drag.current!.x) / maxX) * 100),
      ),
      y: Math.max(
        -100,
        Math.min(100, drag.current!.startY + ((event.clientY - drag.current!.y) / maxY) * 100),
      ),
    }));
  }

  async function confirm() {
    if (!dimensions) return;
    setProcessing(true);
    setProcessingError(null);
    try {
      await onConfirm(await cropImageFile(file, dimensions, transform));
    } catch {
      setProcessingError(t('photoEditor.processingError'));
    } finally {
      setProcessing(false);
    }
  }

  const busy = pending || processing;
  return (
    <div className="flex flex-col gap-5">
      <div className="pr-10">
        <h2 className="font-heading text-xl font-semibold">
          {t('photoEditor.cropTitle', { name: subjectName })}
        </h2>
        <p className="mt-1 text-sm text-foreground-muted">{t('photoEditor.cropHelp')}</p>
      </div>

      <div className="grid items-start gap-5 md:grid-cols-[280px_minmax(0,1fr)]">
        <div
          role="img"
          aria-label={t('photoEditor.preview', { name: subjectName })}
          className="relative mx-auto size-[280px] touch-none cursor-move overflow-hidden rounded-xl bg-surface"
          onPointerDown={(event) => {
            event.currentTarget.setPointerCapture(event.pointerId);
            drag.current = {
              pointerId: event.pointerId,
              x: event.clientX,
              y: event.clientY,
              startX: transform.x,
              startY: transform.y,
            };
          }}
          onPointerMove={movePointer}
          onPointerUp={(event) => {
            event.currentTarget.releasePointerCapture(event.pointerId);
            drag.current = null;
          }}
          onPointerCancel={() => {
            drag.current = null;
          }}
        >
          <img
            src={source}
            alt=""
            draggable={false}
            onLoad={(event) =>
              setDimensions({
                width: event.currentTarget.naturalWidth,
                height: event.currentTarget.naturalHeight,
              })
            }
            className="pointer-events-none absolute max-w-none select-none"
            style={
              layout
                ? {
                    width: layout.width,
                    height: layout.height,
                    left: layout.x,
                    top: layout.y,
                  }
                : undefined
            }
          />
          <div
            aria-hidden="true"
            className="pointer-events-none absolute inset-3 rounded-full border-2 border-white/90 shadow-[0_0_0_999px_rgb(0_0_0/0.42)]"
          />
        </div>

        <div className="flex min-w-0 flex-col gap-4">
          <p className="flex items-center gap-2 text-sm text-foreground-muted">
            <Move aria-hidden="true" className="size-4 shrink-0" />
            {t('photoEditor.dragHelp')}
          </p>
          <label className="flex flex-col gap-2 text-sm font-medium">
            <span className="flex items-center gap-2">
              <ZoomIn aria-hidden="true" className="size-4" />
              {t('photoEditor.zoom')}
            </span>
            <Input
              type="range"
              min="1"
              max="3"
              step="0.01"
              value={transform.zoom}
              onChange={(event) =>
                setTransform((current) => ({ ...current, zoom: Number(event.target.value) }))
              }
            />
          </label>
          <label className="flex flex-col gap-2 text-sm font-medium">
            <span>{t('photoEditor.horizontal')}</span>
            <Input
              type="range"
              min="-100"
              max="100"
              value={transform.x}
              onChange={(event) =>
                setTransform((current) => ({ ...current, x: Number(event.target.value) }))
              }
            />
          </label>
          <label className="flex flex-col gap-2 text-sm font-medium">
            <span>{t('photoEditor.vertical')}</span>
            <Input
              type="range"
              min="-100"
              max="100"
              value={transform.y}
              onChange={(event) =>
                setTransform((current) => ({ ...current, y: Number(event.target.value) }))
              }
            />
          </label>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="self-start"
            onClick={() => setTransform({ zoom: 1.15, x: 0, y: 15 })}
          >
            <RotateCcw aria-hidden="true" className="size-4" />
            {t('photoEditor.reset')}
          </Button>
          <p className="break-all text-xs text-foreground-muted">
            {t('photoEditor.file', { name: file.name })}
          </p>
        </div>
      </div>

      {error || readFailed || processingError ? (
        <p role="alert" className="text-sm text-error">
          {error ?? processingError ?? t('photoEditor.invalid')}
        </p>
      ) : null}
      <div className="flex flex-col-reverse justify-end gap-2 md:flex-row">
        <Button type="button" variant="outline" disabled={busy} onClick={onCancel}>
          {t('common.actions.cancel')}
        </Button>
        <Button
          type="button"
          disabled={busy || !dimensions || readFailed}
          onClick={() => void confirm()}
        >
          {busy ? t('photoEditor.saving') : t('photoEditor.usePhoto')}
        </Button>
      </div>
    </div>
  );
}
