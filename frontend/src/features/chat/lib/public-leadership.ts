/**
 * Humaniza referências antigas à liderança apenas na projeção pública.
 *
 * A ficha da liderança é editável pelo dono e persiste entre versões: uma instância criada antes
 * da humanização carrega para sempre textos como "Operações de IA" no cargo. Reescrever o arquivo
 * apagaria edições legítimas do dono, então a correção vive aqui, na apresentação — que é
 * exatamente onde a regra se aplica. O formulário de edição continua mostrando o texto guardado,
 * senão o dono não conseguiria corrigi-lo de fato.
 */
function humanizeLeadership(text: string): string {
  return text
    .replace(/\bOpera(ç|c)(õ|o)es de IA\b/giu, 'Operações e confiabilidade')
    .replace(/\bprodutos de IA\b/giu, 'produtos digitais')
    .replace(/\bequipe (?:virtual|de IA)\b/giu, 'equipe do projeto')
    .replace(/\bintelig(ê|e)ncia artificial\b/giu, 'tecnologia');
}

/** Campo curto da ficha (cargo, especialidade) exibido ao usuário. */
export function publicLeadershipText(text: string): string {
  return humanizeLeadership(text);
}

/** Conteúdo de uma mensagem publicada pela liderança. */
export function publicLeadershipContent(content: string): string {
  return humanizeLeadership(content)
    .replace(/\bChief operacional e pronto\b/giu, 'Bruna Magalhães está pronta')
    .replace(/\bdo (?:Chief|Chefe)\b/giu, 'de Bruna Magalhães')
    .replace(/\bao (?:Chief|Chefe)\b/giu, 'à Bruna Magalhães')
    .replace(/\bo (?:Chief|Chefe)\b/giu, 'Bruna Magalhães')
    .replace(/\bChief-PSD\b/giu, 'Bruna Magalhães')
    .replace(/\bChief\b/giu, 'Bruna Magalhães')
    .replace(/\bChefe\b/giu, 'Bruna Magalhães');
}
