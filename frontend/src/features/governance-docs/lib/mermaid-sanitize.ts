/**
 * Defesa adicional sobre o `securityLevel: strict` do Mermaid. Mantém apenas
 * SVG local, remove elementos executáveis, handlers inline e links externos.
 */
export function sanitizeMermaidSvg(svg: string): string {
  const document = new DOMParser().parseFromString(svg, 'image/svg+xml');
  if (document.querySelector('parsererror') || document.documentElement.tagName !== 'svg') {
    throw new Error('invalid_mermaid_svg');
  }

  document.querySelectorAll('script, foreignObject, iframe, object, embed').forEach((node) => {
    node.remove();
  });
  document.querySelectorAll('*').forEach((node) => {
    for (const attribute of [...node.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim();
      if (name.startsWith('on')) node.removeAttribute(attribute.name);
      if ((name === 'href' || name === 'xlink:href') && !value.startsWith('#')) {
        node.removeAttribute(attribute.name);
      }
    }
  });

  return document.documentElement.outerHTML;
}
