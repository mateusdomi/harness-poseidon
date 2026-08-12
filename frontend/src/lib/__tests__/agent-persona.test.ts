import { avatarColorsFor, resolveAgentIdentity } from '@/lib/agent-persona';

function channel(value: number) {
  return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
}

function hslLuminance(hue: number, saturationPercent: number, lightnessPercent: number) {
  const saturation = saturationPercent / 100;
  const lightness = lightnessPercent / 100;
  const chroma = (1 - Math.abs(2 * lightness - 1)) * saturation;
  const secondary = chroma * (1 - Math.abs(((hue / 60) % 2) - 1));
  const offset = lightness - chroma / 2;
  const sector = Math.floor(hue / 60);
  const [red, green, blue] = [
    [chroma, secondary, 0],
    [secondary, chroma, 0],
    [0, chroma, secondary],
    [0, secondary, chroma],
    [secondary, 0, chroma],
    [chroma, 0, secondary],
  ][sector];

  return (
    0.2126 * channel(red + offset) +
    0.7152 * channel(green + offset) +
    0.0722 * channel(blue + offset)
  );
}

describe('avatarColorsFor', () => {
  it('mantém contraste AA das iniciais brancas em todo o círculo cromático', () => {
    for (let hue = 0; hue < 360; hue += 1) {
      const contrast = 1.05 / (hslLuminance(hue, 55, 30) + 0.05);
      expect(contrast).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('produz a paleta local determinística usada pelos avatares', () => {
    expect(avatarColorsFor('Mateus')).toEqual({
      background: 'hsl(173 55% 30%)',
      foreground: '#ffffff',
    });
    expect(avatarColorsFor('Mateus')).toEqual(avatarColorsFor('Mateus'));
  });
});

describe('resolveAgentIdentity', () => {
  it('mantém Bruna como perfil público canônico', () => {
    expect(resolveAgentIdentity('chief-orchestrator')).toMatchObject({
      humanName: 'Bruna Magalhães',
      roleLabel: 'Diretora de Engenharia',
    });
  });

  it('não transforma runtime account sem perfil público em pessoa fake', () => {
    expect(resolveAgentIdentity('worker-codex-frontend')).toMatchObject({
      humanName: 'Codex — frontend',
      roleLabel: 'Executor de Projeto',
      alias: 'worker-codex-frontend',
    });
  });

  it('chief runtime account é apresentado como Bruna Magalhães', () => {
    expect(resolveAgentIdentity('chief-claude-primary')).toMatchObject({
      humanName: 'Bruna Magalhães',
      roleLabel: 'Diretora de Engenharia',
      alias: 'chief-claude-primary',
    });
  });
});
