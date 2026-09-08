# Validação executada em 08/09/2026

Ambiente: macOS Apple Silicon, SDK .NET 10.0.400, runtime 10.0.11 e PostgreSQL 17 em Docker local.

| Verificação | Resultado observado |
|---|---|
| Restauração NuGet após atualização das dependências | Sucesso |
| `dotnet build --no-restore -m:1 --disable-build-servers` | Sucesso; zero avisos, zero erros |
| `dotnet test --no-build --no-restore -m:1` | 21 aprovados, zero falhas, zero ignorados; duração informada pelo runner: 2 s |
| `dotnet ef migrations has-pending-model-changes` | Nenhuma mudança pendente |
| Migration `InitialOrders` no banco demo | Aplicada |
| Seed explícito Development | Dois ETFs e duas contas inseridos |
| Kestrel em localhost:5080, `GET /etfs` | 200 com DEMO11 e TEST11 |
| Consulta dos saldos após smoke test | Alice e Bob: 1.000,00 disponíveis, zero reservados |

Relatório bruto local: `TestResults/integration.trx` (ignorado pelo Git). Os testes usam PostgreSQL real em bancos temporários separados e não alteram os saldos do banco demo. O processo HTTP do smoke test foi encerrado; PostgreSQL permanece disponível pelo Compose.

## Evidências dos cenários concorrentes

- 12 solicitações com chaves diferentes, cada uma reservando 300: três aceitas, nove rejeitadas; disponível final 100, reservado 900, três ordens.
- 12 solicitações com a mesma chave e conteúdo: todas recebem a mesma ordem, uma reserva de 200.
- Oito solicitações com mesma chave e quantidades distintas: uma aceita, sete conflitos; apenas a reserva vencedora persiste.
- Antes de liberar a conta, cada teste de disputa observa pelo menos duas sessões esperando por lock no PostgreSQL, comprovando sobreposição.
- Falha injetada após salvar e antes de confirmar a transação: nova leitura encontra saldo inicial e nenhuma ordem; repetir com a mesma chave depois da falha cria normalmente.
- Produção recusa iniciar o mecanismo de identidade demo.

## Ajustes e restrições encontrados

Inicialmente, downloads foram bloqueados pela rede do sandbox; sockets do Docker, test runner e formatador também exigiram execução autorizada fora do sandbox. As permissões foram concedidas e as etapas concluídas. Não ficou bloqueio de ambiente pendente.

A restauração inicial identificou dependência transitiva vulnerável nas ferramentas de design EF. As versões EF foram atualizadas para 10.0.11 e a dependência XML explicitamente corrigida, sem suprimir a auditoria. Foi necessário alinhar também a referência EF Relational para evitar conflito de assemblies nos testes.

A primeira suíte teve 19 aprovações e duas falhas de comparação textual de números JSON (100 versus 100.00). A comparação passou a considerar equivalência numérica; timestamps também foram alinhados aos microssegundos do PostgreSQL. A execução final passou integralmente.

## O que não foi comprovado

Não foram executados testes de carga, medição de p95, múltiplas instâncias da API, queda de processo durante commit, failover ou restauração de backup. Os 2 segundos da suíte não representam latência da API. As metas de 100/1.000 solicitações por segundo, 3.000 consultas por segundo e p95 de 500 ms continuam sendo requisitos a validar. Nenhuma compra foi executada.
