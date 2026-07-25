import { calculateCropLayout } from '@/features/shared/lib/image-crop';

describe('calculateCropLayout', () => {
  it('cobre um recorte quadrado sem distorcer a imagem vertical', () => {
    expect(
      calculateCropLayout(
        { width: 1024, height: 1536 },
        768,
        { zoom: 1, x: 0, y: 0 },
      ),
    ).toEqual({
      width: 768,
      height: 1152,
      x: 0,
      y: -192,
    });
  });

  it('aplica zoom e reposicionamento dentro da sobra recortável', () => {
    const layout = calculateCropLayout(
      { width: 1024, height: 1536 },
      768,
      { zoom: 2, x: 100, y: -100 },
    );

    expect(layout).toEqual({
      width: 1536,
      height: 2304,
      x: 0,
      y: -1536,
    });
  });
});
