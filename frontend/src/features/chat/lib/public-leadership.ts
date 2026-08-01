/** Humaniza referências antigas à liderança apenas na projeção pública da resposta. */
export function publicLeadershipContent(content: string): string {
  return content
    .replace(/\bequipe (?:virtual|de IA)\b/giu, 'equipe do projeto')
    .replace(/\bChief operacional e pronto\b/giu, 'Bruna Magalhães está pronta')
    .replace(/\bdo (?:Chief|Chefe)\b/giu, 'de Bruna Magalhães')
    .replace(/\bao (?:Chief|Chefe)\b/giu, 'à Bruna Magalhães')
    .replace(/\bo (?:Chief|Chefe)\b/giu, 'Bruna Magalhães')
    .replace(/\bChief-PSD\b/giu, 'Bruna Magalhães')
    .replace(/\bChief\b/giu, 'Bruna Magalhães')
    .replace(/\bChefe\b/giu, 'Bruna Magalhães');
}
