/**
 * Baixa o pacote de documentos aprovados do projeto.
 *
 * O navegador precisa de um clique real em um link para salvar o arquivo, e o
 * nome do arquivo vem do servidor (`Content-Disposition`) — quem decide o que
 * saiu e como se chama é quem montou o pacote, não a tela.
 */
export async function downloadProjectDocuments(
  projectId: string,
  baseUrl = '',
): Promise<void> {
  const response = await fetch(
    `${baseUrl}/api/v1/projects/${encodeURIComponent(projectId)}/documents/export`,
    { credentials: 'include' },
  );
  if (!response.ok) {
    throw new Error(`document_export_failed:${response.status}`);
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileNameFrom(response.headers.get('content-disposition'), projectId);
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Nome vindo do servidor; na ausência dele, um nome previsível. */
export function fileNameFrom(disposition: string | null, projectId: string): string {
  const match = disposition?.match(/filename\*?=(?:UTF-8'')?"?([^";]+)"?/i);
  const name = match?.[1] ? decodeURIComponent(match[1]) : null;
  return name && name.endsWith('.zip') ? name : `documentos-${projectId}.zip`;
}
