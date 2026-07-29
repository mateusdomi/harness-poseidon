import { Navigate } from 'react-router-dom';

/**
 * Aprovações deixou de ser tela e virou ATALHO (D9).
 *
 * Aprovações e Documentos sempre operaram o mesmo dado — o mesmo documento, o
 * mesmo portão — em dois destinos diferentes, e o dono tinha de descobrir sozinho
 * que "aprovar" morava fora de Documentos. A fila continua existindo inteira, com
 * a mesma ordenação e os mesmos itens, dentro da aba "Aguardando sua aprovação".
 *
 * O endereço antigo continua válido: quem chega por link, favorito ou pelo card
 * do painel cai direto na aba certa, sem tela intermediária.
 */
export default function UapprovalsPage() {
  return <Navigate to="/documents?tab=approvals" replace />;
}
