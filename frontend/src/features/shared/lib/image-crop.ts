const OUTPUT_SIZE = 768;

export interface ImageDimensions {
  width: number;
  height: number;
}

export interface CropTransform {
  zoom: number;
  x: number;
  y: number;
}

export interface CropLayout {
  width: number;
  height: number;
  x: number;
  y: number;
}

export function calculateCropLayout(
  image: ImageDimensions,
  targetSize: number,
  transform: CropTransform,
): CropLayout {
  const coverScale = Math.max(targetSize / image.width, targetSize / image.height);
  const scale = coverScale * transform.zoom;
  const width = image.width * scale;
  const height = image.height * scale;
  const maxX = Math.max(0, (width - targetSize) / 2);
  const maxY = Math.max(0, (height - targetSize) / 2);
  return {
    width,
    height,
    x: (targetSize - width) / 2 + (transform.x / 100) * maxX,
    y: (targetSize - height) / 2 + (transform.y / 100) * maxY,
  };
}

async function loadImage(source: string): Promise<HTMLImageElement> {
  const image = new Image();
  image.decoding = 'async';
  image.src = source;
  await image.decode();
  return image;
}

export function readImageFile(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result ?? ''));
    reader.onerror = () => reject(reader.error ?? new Error('image_read_failed'));
    reader.onabort = () => reject(new Error('image_read_aborted'));
    reader.readAsDataURL(file);
  });
}

export async function cropImageFile(
  file: File,
  dimensions: ImageDimensions,
  transform: CropTransform,
): Promise<File> {
  const image = await loadImage(await readImageFile(file));
  const canvas = document.createElement('canvas');
  canvas.width = OUTPUT_SIZE;
  canvas.height = OUTPUT_SIZE;
  const context = canvas.getContext('2d');
  if (!context) throw new Error('canvas_unavailable');
  const layout = calculateCropLayout(dimensions, OUTPUT_SIZE, transform);
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = 'high';
  context.drawImage(image, layout.x, layout.y, layout.width, layout.height);
  const blob = await new Promise<Blob | null>((resolve) =>
    canvas.toBlob(resolve, 'image/webp', 0.92),
  );
  if (!blob) throw new Error('image_export_failed');
  const stem = file.name.replace(/\.[^.]+$/, '') || 'foto';
  return new File([blob], `${stem}-recortada.webp`, { type: 'image/webp' });
}
