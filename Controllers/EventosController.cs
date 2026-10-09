using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Hubs;
using RedAJP.Models;
using Stripe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using static RedAJP.Globales.Funciones;

namespace RedAJP.Controllers
{
    public class EventosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly string _stripeSecretKey;
        private IConfiguration _configuration;
        private readonly IHubContext<EventosHub> _eventosHub;
        private Parametros.Modulo Modulo = Parametros.Modulos.Eventos;
        private readonly Cloudinary _cloudinary;

        public EventosController(IConfiguration configuration, IWebHostEnvironment env, IHubContext<EventosHub> eventosHub)
        {
            _configuration = configuration;
            _cadenaConexion = _configuration.GetConnectionString("MiConexion");
            _eventosHub = eventosHub;
            // Configuración Stripe
            _stripeSecretKey = _configuration["StripeEventos:SecretKey"];
            StripeConfiguration.ApiKey = _stripeSecretKey;

            // Configuración Cloudinary
            CloudinaryDotNet.Account account = new CloudinaryDotNet.Account(
                _configuration["Cloudinary:CloudName"],
                _configuration["Cloudinary:ApiKey"],
                _configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        public class DisponibilidadEventoInfo
        {
            public bool EventoActivo { get; set; }
            public string Titulo { get; set; }
            public decimal CostoEntrada { get; set; }
            public bool PermitirPago { get; set; }
            public int CupoMaximoEvento { get; set; }
            public int OcupadosGlobal { get; set; }
            public int DisponiblesGlobal { get; set; }
            public List<DisponibilidadModalidad> Modalidades { get; set; } = new List<DisponibilidadModalidad>();
        }

        public class DisponibilidadModalidad
        {
            public int IdSubtipo { get; set; }
            public string NombreModalidad { get; set; }
            public decimal Costo { get; set; }
            public int CupoTotal { get; set; }
            public int LugaresOcupados { get; set; }
            public int LugaresDisponibles { get; set; }
        }

        /// <summary>
        /// A diferencia de la funcion ConsultarDisponibilidadEvento esta otra funcion se encarga de 
        /// obtener la disponibilidad del evento pero con una logica mas estricta, es decir, 
        /// se consideran como ocupados no solo los registros pagados, sino tambien aquellos que estan en proceso de pago 
        /// o que han registrado su intento de pago en los ultimos 30 minutos. 
        /// Esto ayuda a prevenir sobreventas en eventos con alta demanda y garantiza una experiencia mas fluida 
        /// para los usuarios. Ademas, se puede excluir a ciertos asistentes por ejemplo los de 30 minutos de espera 
        /// para que puedan volver a intentar el pago sin que su cupo quede bloqueado. Esta función es ideal para la etapa final del proceso de compra
        /// pues si no los excluimos les aparecerá el cupo lleno a esos mismos usuarios que están intentando pagar
        /// </summary>
        /// <param name="idEvento"></param>
        /// <param name="conexion"></param>
        /// <param name="transaccion"></param>
        /// <param name="forUpdate"></param>
        /// <param name="idsAsistentesExcluir"></param>
        /// <returns></returns>
        public async Task<DisponibilidadEventoInfo> ConsultarDisponibilidadEvento(int idEvento, NpgsqlConnection conexion = null, NpgsqlTransaction transaccion = null,
            bool forUpdate = false, List<int> idsAsistentesExcluir = null)
        {
            var resultado = new DisponibilidadEventoInfo();
            bool esConexionPropia = false;

            if (conexion == null)
            {
                conexion = new NpgsqlConnection(_cadenaConexion);
                await conexion.OpenAsync();
                esConexionPropia = true;
            }

            try
            {
                string forUpdateSql = forUpdate ? "FOR UPDATE" : "";

                // Construimos el bloque SQL de exclusión dinámicamente
                bool tieneExclusion = idsAsistentesExcluir != null && idsAsistentesExcluir.Any();
                string exclusionSql = tieneExclusion ? "AND b.\"Id_Asistente\" != ALL(@idsExcluir)" : "";

                // A. EVENTO GLOBAL
                string sqlGlobal = $@"
            SELECT 
                e.""Activo"", e.""Titulo"", e.""Costo_Entrada"", e.""Permitir_Pago"", e.""Cupo_Maximo"", 
                (
                    SELECT COUNT(DISTINCT b.""Id_Asistente"") 
                    FROM ""Eventos_B_Asistentes"" b 
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                    LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON c.""Id_Asistente"" = b.""Id_Asistente""
                    LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                    LEFT JOIN ""Eventos_D_Transacciones"" t ON t.""Id_Transaccion"" = pa.""Id_Transaccion""
                    WHERE r.""Id_Evento"" = e.""Id_Evento""
                    {exclusionSql} -- <--- INYECCIÓN DE LA EXCLUSIÓN
                    AND (
                        b.""Es_Pagado"" = TRUE 
                        OR t.""Estatus_Pago"" IN ('paid', 'processing', 'waiting_proof', 'review') 
                        OR t.""EsTransferencia"" = TRUE
                        OR (r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes')
                        OR (t.""Estatus_Pago"" = 'pending' AND t.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes')
                    )
                ) as ""Ocupado"" 
            FROM ""Eventos_Catalogo"" e
            WHERE e.""Id_Evento"" = @id {forUpdateSql}";

                using (var cmdG = new NpgsqlCommand(sqlGlobal, conexion, transaccion))
                {
                    cmdG.Parameters.AddWithValue("@id", idEvento);
                    if (tieneExclusion) cmdG.Parameters.AddWithValue("@idsExcluir", idsAsistentesExcluir.ToArray());

                    using (var r = await cmdG.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            resultado.EventoActivo = (bool)r["Activo"];
                            resultado.Titulo = r["Titulo"].ToString();
                            resultado.CostoEntrada = (decimal)r["Costo_Entrada"];
                            resultado.PermitirPago = (bool)r["Permitir_Pago"];
                            resultado.CupoMaximoEvento = (int)r["Cupo_Maximo"];
                            resultado.OcupadosGlobal = Convert.ToInt32(r["Ocupado"]);

                            resultado.DisponiblesGlobal = resultado.CupoMaximoEvento - resultado.OcupadosGlobal;
                            if (resultado.DisponiblesGlobal < 0) resultado.DisponiblesGlobal = 0;
                        }
                        else return null;
                    }
                }

                // B. MODALIDADES (SUBTIPOS)
                string sqlSub = $@"
            SELECT 
                s.""Id_Subtipo"", s.""Nombre"", s.""Cupo"", s.""Costo"",
                (
                    SELECT COUNT(DISTINCT b.""Id_Asistente"")
                    FROM ""Eventos_B_Asistentes"" b
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                    LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON c.""Id_Asistente"" = b.""Id_Asistente""
                    LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                    LEFT JOIN ""Eventos_D_Transacciones"" t ON t.""Id_Transaccion"" = pa.""Id_Transaccion""
                    WHERE r.""Id_Evento"" = @id AND b.""Id_Subtipo"" = s.""Id_Subtipo""
                    {exclusionSql} -- <--- INYECCIÓN DE LA EXCLUSIÓN
                    AND (
                        b.""Es_Pagado"" = TRUE
                        OR t.""Estatus_Pago"" IN ('paid', 'processing', 'waiting_proof', 'review')
                        OR t.""EsTransferencia"" = TRUE
                        OR (r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes')
                        OR (t.""Estatus_Pago"" = 'pending' AND t.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes')
                    )
                ) as ""Ocupados""
            FROM ""Eventos_Subtipos"" s
            WHERE s.""Id_Evento"" = @id AND s.""Activo"" = TRUE
            ORDER BY s.""Costo"" ASC, s.""Nombre"" ASC {forUpdateSql}";

                using (var cmdS = new NpgsqlCommand(sqlSub, conexion, transaccion))
                {
                    cmdS.Parameters.AddWithValue("@id", idEvento);
                    if (tieneExclusion) cmdS.Parameters.AddWithValue("@idsExcluir", idsAsistentesExcluir.ToArray());

                    using (var r = await cmdS.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int cupoTotal = (int)r["Cupo"];
                            int ocupados = Convert.ToInt32(r["Ocupados"]);
                            int disponibles = cupoTotal - ocupados;
                            if (disponibles < 0) disponibles = 0;

                            resultado.Modalidades.Add(new DisponibilidadModalidad
                            {
                                IdSubtipo = (int)r["Id_Subtipo"],
                                NombreModalidad = r["Nombre"].ToString(),
                                Costo = (decimal)r["Costo"],
                                CupoTotal = cupoTotal,
                                LugaresOcupados = ocupados,
                                LugaresDisponibles = disponibles
                            });
                        }
                    }
                }
            }
            finally
            {
                // Solo cerramos la conexión si la función la abrió
                if (esConexionPropia && conexion != null)
                {
                    await conexion.CloseAsync();
                    conexion.Dispose();
                }
            }

            return resultado;
        }
        /// <summary>
        /// Aplica un pago exitoso a la transacción, calculando comisiones y actualizando estados de forma atómica.
        /// </summary>
        private async Task AplicarPagoExitoso(int idTransaccion, string refStripePaymentIntent)
        {
            // Usamos una conexión nueva para asegurar aislamiento en esta operación crítica
            using var con = new NpgsqlConnection(_cadenaConexion);
            await con.OpenAsync();

            // 1. RECUPERAR DATOS PREVIOS
            // Necesitamos saber si es transferencia y el monto bruto original para los cálculos financieros.
            bool esTransferencia = false;
            decimal montoTotalBruto = 0;

            string sqlCheck = @"SELECT ""Completado"", ""EsTransferencia"", ""Monto_Total"" 
                        FROM ""Eventos_D_Transacciones"" 
                        WHERE ""Id_Transaccion"" = @id";

            using (var cmdCheck = new NpgsqlCommand(sqlCheck, con))
            {
                cmdCheck.Parameters.AddWithValue("@id", idTransaccion);
                using (var r = await cmdCheck.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        // Validación rápida: si ya estaba completado, no hacemos nada (Idempotencia de lectura)
                        if (r["Completado"] != DBNull.Value && (bool)r["Completado"]) return;

                        esTransferencia = r["EsTransferencia"] != DBNull.Value && (bool)r["EsTransferencia"];
                        montoTotalBruto = (decimal)r["Monto_Total"];
                    }
                    else return; // Transacción no existe
                }
            }

            // 2. CÁLCULO DE COSTO REAL / NETO
            decimal montoNetoCalculado = 0;

            if (esTransferencia)
            {
                // CASO A: TRANSFERENCIA (Neto = Bruto, no hay comisión de pasarela)
                montoNetoCalculado = montoTotalBruto;
            }
            else
            {
                // CASO B: STRIPE (Calculamos Comisión consultando la API)
                try
                {
                    if (!string.IsNullOrEmpty(refStripePaymentIntent))
                    {
                        if (refStripePaymentIntent.StartsWith("cs_"))
                        {
                            var service = new Stripe.Checkout.SessionService();
                            var options = new Stripe.Checkout.SessionGetOptions();
                            options.AddExpand("payment_intent.latest_charge.balance_transaction");
                            var session = await service.GetAsync(refStripePaymentIntent, options);

                            if (session.PaymentIntent?.LatestCharge?.BalanceTransaction != null)
                                montoNetoCalculado = session.PaymentIntent.LatestCharge.BalanceTransaction.Net / 100.0m;
                            else
                                montoNetoCalculado = 0; // Bandera secreta, Stripe va lento
                        }
                        else if (refStripePaymentIntent.StartsWith("pi_"))
                        {
                            var service = new Stripe.PaymentIntentService();
                            var options = new Stripe.PaymentIntentGetOptions();
                            options.AddExpand("latest_charge.balance_transaction");
                            var intent = await service.GetAsync(refStripePaymentIntent, options);

                            if (intent.LatestCharge?.BalanceTransaction != null)
                                montoNetoCalculado = intent.LatestCharge.BalanceTransaction.Net / 100.0m;
                            else
                                montoNetoCalculado = 0; // Bandera secreta, Stripe va lento
                        }
                        else
                        {
                            // Aprobaciones manuales del Admin (ej. APROBADO_ADMIN...)
                            montoNetoCalculado = montoTotalBruto;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error calculando comisión Stripe: " + ex.Message);
                    montoNetoCalculado = 0; // CORRECCIÓN: Bandera secreta en caso de error de conexión
                }
            }

            // CORRECCIÓN: Se eliminó la línea "if (montoNetoCalculado <= 0) montoNetoCalculado = montoTotalBruto;"
            // para permitir que el 0 fluya a la base de datos y pueda ser auditado después.

            // 3. APLICACIÓN TRANSACCIONAL (Arquitectura Nueva + Lógica Original)
            using var trans = await con.BeginTransactionAsync();
            try
            {
                // A. GATEKEEPER ATÓMICO (Mejora Técnica: UPDATE RETURNING)
                // Actualizamos Estatus, Referencia y el Total_Neto calculado.
                // Si "Completado" ya era TRUE, esto devuelve null y salimos.
                string sqlGuard = @"
            UPDATE ""Eventos_D_Transacciones"" 
            SET ""Completado"" = TRUE, 
                ""Estatus_Pago"" = 'paid', 
                ""Ref_Pasarela"" = @ref,
                ""Total_Neto"" = @neto,
                ""Fecha_Pago_Aceptado"" = NOW()
            WHERE ""Id_Transaccion"" = @id AND ""Completado"" = FALSE
            RETURNING ""Id_Transaccion""";

                using (var cmdGuard = new NpgsqlCommand(sqlGuard, con, trans))
                {
                    cmdGuard.Parameters.AddWithValue("@ref", (object)refStripePaymentIntent ?? DBNull.Value);
                    cmdGuard.Parameters.AddWithValue("@neto", montoNetoCalculado);
                    cmdGuard.Parameters.AddWithValue("@id", idTransaccion);

                    var res = await cmdGuard.ExecuteScalarAsync();
                    if (res == null)
                    {
                        // Otro proceso ganó la carrera. Rollback y salir.
                        await trans.RollbackAsync();
                        return;
                    }
                }

                // B. MARCAR CUENTAS COMO PAGADAS (Tabla C)
                string sqlUpdateCuentas = @"
            UPDATE ""Eventos_C_Cuentas_Cobrar""
            SET ""Pagado"" = TRUE, ""Fecha_Pagado"" = NOW()
            WHERE ""Id_Cuenta"" IN (
                SELECT ""Id_Cuenta"" FROM ""Eventos_E_Pagos_Aplicados"" WHERE ""Id_Transaccion"" = @id
            )";
                using (var cmdC = new NpgsqlCommand(sqlUpdateCuentas, con, trans))
                {
                    cmdC.Parameters.AddWithValue("@id", idTransaccion);
                    await cmdC.ExecuteNonQueryAsync();
                }

                // C. ACTUALIZAR ESTADO DE ASISTENTES (Restaurado Lógica Compleja de Tabla B)
                // Un asistente solo es "Es_Pagado" si NO tiene ninguna cuenta pendiente asociada.
                string sqlActualizarB = @"
            UPDATE ""Eventos_B_Asistentes"" b
            SET ""Es_Pagado"" = (
                NOT EXISTS (
                    SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c 
                    WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE
                )
            )
            WHERE ""Id_Asistente"" IN (
                SELECT DISTINCT c.""Id_Asistente""
                FROM ""Eventos_E_Pagos_Aplicados"" e
                JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                WHERE e.""Id_Transaccion"" = @id
            )";
                using (var cmdB = new NpgsqlCommand(sqlActualizarB, con, trans))
                {
                    cmdB.Parameters.AddWithValue("@id", idTransaccion);
                    await cmdB.ExecuteNonQueryAsync();
                }

                // D. BITÁCORA (Restaurado)
                // Registramos la acción para auditoría. Usamos ID 1 (Sistema).
                await Funciones.RegistrarBitacora(con, 1, Modulo,
                    Parametros.AccionesBitacora.Editar,
                    $"Sistema aplicó pago exitoso a Transacción #{idTransaccion}. Ref: {refStripePaymentIntent}. Neto: {montoNetoCalculado}",
                    "Sistema", trans);

                await trans.CommitAsync();
                Console.WriteLine($"Transacción {idTransaccion} aplicada con éxito.");
            }
            catch (Exception ex)
            {
                await trans.RollbackAsync();
                Console.WriteLine($"Error crítico en AplicarPagoExitoso {idTransaccion}: " + ex.Message);
                throw; // Re-lanzar para que VerificarPagosPendientes pueda revertir el estado 'processing'
            }
        }

        /// <summary>
        /// Endpoint para limpiar la "basura" de Stripe.
        /// Solo expira las transacciones abandonadas o canceladas Y libera sus cupones.
        /// NO procesa pagos exitosos.
        /// </summary>
        [HttpGet("Eventos/LimpiarExpiradosStripe")]
        [Authorize]
        public async Task<IActionResult> LimpiarExpiradosStripe()
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, PermisoAdmin)) return Forbid();

            var resultados = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var transaccionesPerdidas = new List<dynamic>();
                    // Agregamos Id_Cupon y Id_Usuario a la lectura para poder liberarlos
                    string sqlGetPerdidas = @"
                SELECT ""Id_Transaccion"", ""Ref_Pasarela"", ""Id_Cupon"", ""Id_Usuario""
                FROM ""Eventos_D_Transacciones""
                WHERE (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')
                  AND ""Completado"" = FALSE
                  AND ""Estatus_Pago"" = 'pending'";

                    using (var cmd = new NpgsqlCommand(sqlGetPerdidas, conexion))
                    {
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                transaccionesPerdidas.Add(new
                                {
                                    IdTrx = (int)r["Id_Transaccion"],
                                    RefPasarela = r["Ref_Pasarela"].ToString(),
                                    IdCupon = r["Id_Cupon"] != DBNull.Value ? (int?)r["Id_Cupon"] : null,
                                    IdUsuario = (int)r["Id_Usuario"]
                                });
                            }
                        }
                    }

                    var sessionService = new Stripe.Checkout.SessionService();
                    var piService = new Stripe.PaymentIntentService();

                    foreach (var trx in transaccionesPerdidas)
                    {
                        string estatusStripeRaw = "";
                        string accionTomada = "Ninguna";

                        try
                        {
                            bool debeExpirar = false;

                            // A. Evaluar Sesiones (cs_)
                            if (trx.RefPasarela.StartsWith("cs_"))
                            {
                                var session = await sessionService.GetAsync(trx.RefPasarela);
                                estatusStripeRaw = session.Status;

                                if (session.Status == "expired" || session.Status == "canceled")
                                    debeExpirar = true;
                                else if (session.PaymentStatus == "paid")
                                    accionTomada = "Ignorado (Dejado para VerificarPagosPendientes)";
                            }
                            // B. Evaluar Payment Intents (pi_)
                            else if (trx.RefPasarela.StartsWith("pi_"))
                            {
                                var pi = await piService.GetAsync(trx.RefPasarela);
                                estatusStripeRaw = pi.Status;

                                if (pi.Status == "canceled")
                                    debeExpirar = true;
                                else if (pi.Status == "succeeded")
                                    accionTomada = "Ignorado (Dejado para VerificarPagosPendientes)";
                            }

                            // C. Ejecutar la expiración de forma transaccional para proteger los cupones
                            if (debeExpirar)
                            {
                                using (var trans = await conexion.BeginTransactionAsync())
                                {
                                    try
                                    {
                                        // 1. Liberar el cupón si existía
                                        if (trx.IdCupon != null)
                                        {
                                            string sqlStock = @"UPDATE ""Sist_Cupones"" SET ""Conteo_Usados"" = GREATEST(""Conteo_Usados"" - 1, 0) WHERE ""Id_Cupon"" = @idC";
                                            using (var cmdS = new NpgsqlCommand(sqlStock, conexion, trans))
                                            {
                                                cmdS.Parameters.AddWithValue("@idC", trx.IdCupon);
                                                await cmdS.ExecuteNonQueryAsync();
                                            }

                                            string sqlUnlock = @"DELETE FROM ""Sist_Cupones_Uso"" WHERE ""Id_Cupon"" = @idC AND ""Id_Usuario"" = @uid";
                                            using (var cmdD = new NpgsqlCommand(sqlUnlock, conexion, trans))
                                            {
                                                cmdD.Parameters.AddWithValue("@idC", trx.IdCupon);
                                                cmdD.Parameters.AddWithValue("@uid", trx.IdUsuario);
                                                await cmdD.ExecuteNonQueryAsync();
                                            }
                                        }

                                        // 2. Marcar la transacción como muerta
                                        string sqlFixTrx = @"
                                    UPDATE ""Eventos_D_Transacciones"" 
                                    SET ""Estatus_Pago"" = 'expired', 
                                        ""Completado"" = TRUE
                                    WHERE ""Id_Transaccion"" = @idT";

                                        using (var cmdFix = new NpgsqlCommand(sqlFixTrx, conexion, trans))
                                        {
                                            cmdFix.Parameters.AddWithValue("@idT", trx.IdTrx);
                                            await cmdFix.ExecuteNonQueryAsync();
                                        }

                                        await trans.CommitAsync();
                                        accionTomada = trx.IdCupon != null ? "EXPIRED y Cupón Liberado" : "Marcado como EXPIRED";
                                    }
                                    catch (Exception)
                                    {
                                        await trans.RollbackAsync();
                                        throw;
                                    }
                                }
                            }

                            resultados.Add(new { IdLocal = trx.IdTrx, RefStripe = trx.RefPasarela, StripeDijo = estatusStripeRaw, Accion = accionTomada });
                        }
                        catch (Exception ex)
                        {
                            resultados.Add(new { IdLocal = trx.IdTrx, Error = ex.Message });
                        }
                    }
                }

                return Json(new { mensaje = "Limpieza de expirados finalizada.", total_evaluados = resultados.Count, detalle = resultados });
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Orquestador de verificación de pagos.
        /// Se encarga de limpiar estados inválidos, auditar comisiones y 
        /// validar con Stripe los pagos pendientes globales.
        /// </summary>
        private async Task VerificarPagosPendientes()
        {
            try
            {
                // Evaluamos si el usuario en sesión es administrador
                bool esAdmin = User.TienePermiso(Parametros.Modulos.Eventos, PermisoAdmin);
                string limiteSql = esAdmin ? "" : "LIMIT 10";

                using var con = new NpgsqlConnection(_cadenaConexion);
                await con.OpenAsync();

                // ---------------------------------------------------------
                // 1. RESCATE DE TRANSACCIONES HUÉRFANAS GLOBAL
                // ---------------------------------------------------------
                string sqlRescue = @"
            UPDATE ""Eventos_D_Transacciones""
            SET ""Estatus_Pago"" = 'pending',
                ""Fecha_Proceso"" = NULL
            WHERE ""Estatus_Pago"" = 'processing' 
              AND ""Fecha_Proceso"" < (NOW() - INTERVAL '60 minutes')
              AND ""Completado"" = FALSE";

                using (var cmdRescue = new NpgsqlCommand(sqlRescue, con))
                {
                    await cmdRescue.ExecuteNonQueryAsync();
                }


                // ---------------------------------------------------------
                // 2. AUDITORÍA SILENCIOSA DE COMISIONES FALTANTES (Corregida)
                // ---------------------------------------------------------
                try
                {
                    // Solo busca pagos completados, sin comision y que NO estén expirados o cancelados
                    string sqlAuditoria = $@"
                SELECT ""Id_Transaccion"", ""Ref_Pasarela"" 
                FROM ""Eventos_D_Transacciones"" 
                WHERE ""Completado"" = TRUE 
                  AND ""Total_Neto"" = 0 
                  AND ""Estatus_Pago"" = 'paid'
                  AND (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')
                ORDER BY ""Id_Transaccion"" ASC
                {limiteSql}";

                    var pagosPorAuditar = new List<dynamic>();
                    using (var cmdAud = new NpgsqlCommand(sqlAuditoria, con))
                    {
                        using var rAud = await cmdAud.ExecuteReaderAsync();
                        while (await rAud.ReadAsync())
                        {
                            pagosPorAuditar.Add(new
                            {
                                IdTrx = (int)rAud["Id_Transaccion"],
                                SessionId = rAud["Ref_Pasarela"].ToString()
                            });
                        }
                    }

                    if (pagosPorAuditar.Any())
                    {
                        var sessionServiceAud = new Stripe.Checkout.SessionService();
                        var piServiceAud = new Stripe.PaymentIntentService();

                        foreach (var p in pagosPorAuditar)
                        {
                            decimal netoReal = 0;
                            string refPasarela = p.SessionId;

                            if (refPasarela.StartsWith("cs_"))
                            {
                                var sessionAud = await sessionServiceAud.GetAsync(refPasarela);
                                if (!string.IsNullOrEmpty(sessionAud.PaymentIntentId))
                                {
                                    var optionsPI = new Stripe.PaymentIntentGetOptions();
                                    optionsPI.AddExpand("latest_charge.balance_transaction");
                                    var piAud = await piServiceAud.GetAsync(sessionAud.PaymentIntentId, optionsPI);

                                    if (piAud.LatestCharge?.BalanceTransaction != null)
                                        netoReal = piAud.LatestCharge.BalanceTransaction.Net / 100.0m;
                                }
                            }
                            else if (refPasarela.StartsWith("pi_"))
                            {
                                var optionsPI = new Stripe.PaymentIntentGetOptions();
                                optionsPI.AddExpand("latest_charge.balance_transaction");
                                var piAud = await piServiceAud.GetAsync(refPasarela, optionsPI);

                                if (piAud.LatestCharge?.BalanceTransaction != null)
                                    netoReal = piAud.LatestCharge.BalanceTransaction.Net / 100.0m;
                            }

                            if (netoReal > 0)
                            {
                                string sqlFix = @"UPDATE ""Eventos_D_Transacciones"" SET ""Total_Neto"" = @neto WHERE ""Id_Transaccion"" = @id";
                                using (var cmdFix = new NpgsqlCommand(sqlFix, con))
                                {
                                    cmdFix.Parameters.AddWithValue("@neto", netoReal);
                                    cmdFix.Parameters.AddWithValue("@id", p.IdTrx);
                                    await cmdFix.ExecuteNonQueryAsync();
                                }
                            }
                        }
                    }
                }
                catch (Exception exAud) { Console.WriteLine("Error en auditoría silenciosa Eventos: " + exAud.Message); }

                // ---------------------------------------------------------
                // 3. OBTENER CANDIDATOS A PROCESAR (Globalizado y con Límite)
                // ---------------------------------------------------------
                var pendientes = new List<dynamic>();

                // Se quitó el filtro de Id_Usuario y se aplicó el límite dinámico
                string sqlSelect = $@"
            SELECT ""Id_Transaccion"", ""Ref_Pasarela"", ""External_Reference"", ""Monto_Total""
            FROM ""Eventos_D_Transacciones""
            WHERE ""Completado"" = FALSE
              AND ""Estatus_Pago"" = 'pending'
              AND (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')
              AND ""Fecha_Intento"" >= (NOW() - INTERVAL '3 days')
            ORDER BY ""Id_Transaccion"" ASC
            {limiteSql}";

                using (var cmd = new NpgsqlCommand(sqlSelect, con))
                {
                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        pendientes.Add(new
                        {
                            IdTrx = (int)r["Id_Transaccion"],
                            SessionId = r["Ref_Pasarela"]?.ToString(),
                            RefEsperada = r["External_Reference"]?.ToString(),
                            MontoEsperado = r["Monto_Total"] != DBNull.Value ? Convert.ToDecimal(r["Monto_Total"]) : 0m
                        });
                    }
                }

                if (!pendientes.Any()) return;

                // ---------------------------------------------------------
                // 4. BUCLE DE VALIDACIÓN CON STRIPE
                // ---------------------------------------------------------
                var sessionService = new Stripe.Checkout.SessionService();
                var piService = new Stripe.PaymentIntentService();

                foreach (var p in pendientes)
                {
                    try
                    {
                        Stripe.PaymentIntent pi = null;
                        string refPasarela = p.SessionId;

                        if (refPasarela.StartsWith("cs_"))
                        {
                            var session = await sessionService.GetAsync(refPasarela);
                            if (session.PaymentStatus != "paid" || string.IsNullOrEmpty(session.PaymentIntentId)) continue;

                            pi = await piService.GetAsync(session.PaymentIntentId);
                            if (pi.Status != "succeeded") continue;

                            if (p.RefEsperada != null && session.ClientReferenceId != p.RefEsperada)
                                continue;
                        }
                        else if (refPasarela.StartsWith("pi_"))
                        {
                            pi = await piService.GetAsync(refPasarela);
                            if (pi.Status != "succeeded") continue;
                        }

                        if (pi == null) continue;

                        // VALIDACIÓN DE SEGURIDAD: Monto Cobrado vs Esperado
                        decimal montoRecibido = pi.AmountReceived / 100.0m;

                        if (Math.Round(montoRecibido, 2) != Math.Round(p.MontoEsperado, 2))
                            continue; // Fraude o error de montos, ignorar

                        // CLAIM ATÓMICO (EL SEMÁFORO)
                        bool claimed = false;
                        using (var claimCon = new NpgsqlConnection(_cadenaConexion))
                        {
                            await claimCon.OpenAsync();
                            string sqlClaim = @"
                        UPDATE ""Eventos_D_Transacciones"" 
                        SET ""Estatus_Pago"" = 'processing',
                            ""Fecha_Proceso"" = NOW()
                        WHERE ""Id_Transaccion"" = @id 
                          AND ""Completado"" = FALSE 
                          AND ""Estatus_Pago"" = 'pending' 
                        RETURNING ""Id_Transaccion""";

                            using var cmdClaim = new NpgsqlCommand(sqlClaim, claimCon);
                            cmdClaim.Parameters.AddWithValue("@id", (int)p.IdTrx);
                            claimed = await cmdClaim.ExecuteScalarAsync() != null;
                        }

                        if (!claimed) continue; // Skip, otro worker lo tiene

                        // APLICACIÓN DEL PAGO (HANDOFF)
                        try
                        {
                            await AplicarPagoExitoso(p.IdTrx, pi.Id);
                        }
                        catch (Exception exApply)
                        {
                            Console.WriteLine($"Error aplicando pago {p.IdTrx}: {exApply.Message}");

                            using var revertCon = new NpgsqlConnection(_cadenaConexion);
                            await revertCon.OpenAsync();
                            string sqlRevert = @"
                        UPDATE ""Eventos_D_Transacciones"" 
                        SET ""Estatus_Pago"" = 'pending', ""Fecha_Proceso"" = NULL 
                        WHERE ""Id_Transaccion"" = @id AND ""Estatus_Pago"" = 'processing'";

                            using var cmdRev = new NpgsqlCommand(sqlRevert, revertCon);
                            cmdRev.Parameters.AddWithValue("@id", (int)p.IdTrx);
                            await cmdRev.ExecuteNonQueryAsync();
                        }
                    }
                    catch (Exception exStripe)
                    {
                        Console.WriteLine($"Error procesando pendiente {p.IdTrx}: {exStripe.Message}");
                    }
                }
            }
            catch (Exception exGlobal)
            {
                Console.WriteLine($"Error fatal en VerificarPagosPendientes: {exGlobal.Message}");
            }
        }

        /// <summary>
        /// Endpoint AJAX para validar un cupón antes del pago.
        /// Verifica existencia, vigencia, stock, montos mínimos y restricciones de usuario.
        /// </summary>
        /// <param name="codigo">El código alfanumérico del cupón.</param>
        /// <param name="montoCompra">El total actual de la compra (Subtotal original).</param>
        /// <param name="idEvento">El ID del evento (opcional) para validar alcance.</param>
        /// <param name="aplicarComision">Indica si se debe calcular la comisión de pasarela sobre el nuevo total.</param>
        /// <returns>JSON con el resultado de la validación y el nuevo total calculado.</returns>
        [HttpPost]
        [AllowAnonymous] // Se permite el acceso al endpoint, pero validamos la sesión manualmente dentro
        public async Task<IActionResult> ValidarCuponEventos(string codigo, decimal montoCompra, int? idEvento = null, bool aplicarComision = false)
        {
            if (string.IsNullOrWhiteSpace(codigo)) return Json(new { valido = false, mensaje = "Código vacío." });

            // 1. REGLA DE ORO: VALIDACIÓN DE USUARIO LOGUEADO
            // Tal como pediste, esto no se toca. Si no hay sesión, no hay cupón.
            if (!User.Identity.IsAuthenticated)
            {
                return Json(new { valido = false, mensaje = "Debes iniciar sesión para canjear un cupón.", requiereLogin = true });
            }

            try
            {
                var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
                string cadenaConexion = configuration.GetConnectionString("MiConexion");

                // Obtenemos el ID del usuario logueado
                int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

                using (var conexion = new NpgsqlConnection(cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 2. OBTENCIÓN DE DATOS Y VALIDACIÓN DE LISTA (EN UNA SOLA CONSULTA)
                    // Ya no buscamos 'Email_Restringido'. Ahora contamos registros en la tabla hija.
                    var datosCupon = new
                    {
                        Id = 0,
                        Tipo = 0,
                        Valor = 0m,
                        Tope = (decimal?)null,
                        Minimo = 0m,
                        Limite = 0,
                        Usados = 0,
                        AplicaEventos = false,
                        IdEventoRestringido = (int?)null,
                        // Nuevos Flags calculados en SQL
                        TieneListaRestrictiva = false,
                        UsuarioEstaEnLista = false
                    };

                    string sql = @"SELECT c.""Id_Cupon"", c.""Tipo_Descuento"", c.""Valor"", c.""Tope_Maximo_Descuento"", 
                                  c.""Monto_Minimo_Compra"", c.""Limite_Usos"", c.""Conteo_Usados"", 
                                  c.""Aplica_Eventos"", c.""Id_Evento_Restringido"",
                                  
                                  -- Subconsulta 1: ¿Este cupón tiene usuarios asignados?
                                  (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalEnLista"",

                                  -- Subconsulta 2: ¿El usuario actual está en esa lista?
                                  (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"" AND u.""Id_Usuario"" = @uid) as ""SoyYo""

                           FROM ""Sist_Cupones"" c 
                           WHERE UPPER(c.""Codigo"") = UPPER(@cod) 
                           AND c.""Activo"" = TRUE 
                           AND NOW() BETWEEN c.""Fecha_Inicio"" AND c.""Fecha_Fin""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@cod", codigo.Trim());
                        cmd.Parameters.AddWithValue("@uid", idUsuarioActual); // Pasamos el ID del usuario logueado

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                datosCupon = new
                                {
                                    Id = (int)r["Id_Cupon"],
                                    Tipo = (int)r["Tipo_Descuento"],
                                    Valor = (decimal)r["Valor"],
                                    Tope = r["Tope_Maximo_Descuento"] as decimal?,
                                    Minimo = (decimal)r["Monto_Minimo_Compra"],
                                    Limite = (int)r["Limite_Usos"],
                                    Usados = (int)r["Conteo_Usados"],
                                    AplicaEventos = (bool)r["Aplica_Eventos"],
                                    IdEventoRestringido = r["Id_Evento_Restringido"] as int?,

                                    // Lógica: Si TotalEnLista > 0, es un cupón privado.
                                    TieneListaRestrictiva = Convert.ToInt64(r["TotalEnLista"]) > 0,
                                    // Lógica: Si SoyYo > 0, tengo permiso.
                                    UsuarioEstaEnLista = Convert.ToInt64(r["SoyYo"]) > 0
                                };
                            }
                            else return Json(new { valido = false, mensaje = "Cupón no válido o expirado." });
                        }
                    }

                    // 3. VALIDACIONES DE REGLAS DE NEGOCIO

                    // A. Stock y Mínimos
                    if (datosCupon.Usados >= datosCupon.Limite) return Json(new { valido = false, mensaje = "Este cupón se ha agotado." });
                    if (montoCompra < datosCupon.Minimo) return Json(new { valido = false, mensaje = $"La compra mínima es de ${datosCupon.Minimo:N2}." });


                    // 1. VALIDACIÓN MAESTRA: ¿El cupón sirve para eventos?
                    // Esta validación se ejecuta SIEMPRE.
                    if (!datosCupon.AplicaEventos)
                    {
                        return Json(new { valido = false, mensaje = "Este cupón es exclusivo para la Tienda y no aplica en Eventos." });
                    }

                    // 2. Validación Específica: ¿Sirve para ESTE evento en particular?
                    // Solo si recibimos un ID válido (> 0) comparamos la restricción.
                    if (idEvento > 0)
                    {
                        if (datosCupon.IdEventoRestringido.HasValue && datosCupon.IdEventoRestringido.Value != idEvento)
                        {
                            return Json(new { valido = false, mensaje = "Este cupón no aplica para este evento específico." });
                        }
                    }

                    // C. VALIDACIÓN DE PERTENENCIA (LISTA DE USUARIOS)
                    // Si el cupón tiene una lista definida, y el usuario NO está en ella -> Error.
                    if (datosCupon.TieneListaRestrictiva && !datosCupon.UsuarioEstaEnLista)
                    {
                        return Json(new { valido = false, mensaje = "Este cupón es exclusivo y tu cuenta no está en la lista autorizada." });
                    }

                    // D. Validación de Uso Único (Histórico)
                    // Verificamos si este usuario específico ya quemó este cupón antes.
                    string sqlHist = @"SELECT COUNT(*) FROM ""Sist_Cupones_Uso"" WHERE ""Id_Cupon"" = @idC AND ""Id_Usuario"" = @idU";
                    using (var cmdH = new NpgsqlCommand(sqlHist, conexion))
                    {
                        cmdH.Parameters.AddWithValue("@idC", datosCupon.Id);
                        cmdH.Parameters.AddWithValue("@idU", idUsuarioActual);
                        if ((long)await cmdH.ExecuteScalarAsync() > 0)
                        {
                            return Json(new { valido = false, mensaje = "Ya utilizaste este cupón anteriormente." });
                        }
                    }

                    // 4. CÁLCULO FINAL
                    decimal descuento = Funciones.CalcularMontoDescuento(montoCompra, datosCupon.Tipo, datosCupon.Valor, datosCupon.Tope);
                    decimal baseConDescuento = montoCompra - descuento;
                    if (baseConDescuento < 0) baseConDescuento = 0;

                    decimal comision = 0;
                    decimal totalFinal = baseConDescuento;
                    
                    bool cobrarComisionExtra = true;
                    if (idEvento.HasValue)
                    {
                        string sqlCE = @"SELECT ""Cobrar_Comision_Extra"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @idE";
                        using (var cmdCE = new NpgsqlCommand(sqlCE, conexion))
                        {
                            cmdCE.Parameters.AddWithValue("@idE", idEvento.Value);
                            var resObj = await cmdCE.ExecuteScalarAsync();
                            if (resObj != null && resObj != DBNull.Value) cobrarComisionExtra = (bool)resObj;
                        }
                    }

                    if (aplicarComision && cobrarComisionExtra)
                    {
                        totalFinal = Funciones.CalcularPagoConComision(baseConDescuento, configuration);
                        comision = totalFinal - baseConDescuento;
                    }

                    return Json(new
                    {
                        valido = true,
                        descuento = descuento,
                        nuevoSubtotal = baseConDescuento,
                        comision = comision,
                        nuevoTotal = totalFinal,
                        mensaje = "Cupón aplicado correctamente."
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { valido = false, mensaje = "Error al validar: " + ex.Message });
            }
        }
        /// <summary>
        /// Genera un token amigable para el pago externo basado en el nombre completo del asistente.
        /// </summary>
        /// <param name="nombreCompleto">Es el nombre completo del asistente.</param>
        /// <returns>Retorna el token generado.</returns>
        private string GenerarTokenAmigable(string nombreCompleto)
        {
            string nombreLimpio = "INVITADO";
            if (!string.IsNullOrEmpty(nombreCompleto))
            {
                // Solo letras y números, mayúsculas
                nombreLimpio = System.Text.RegularExpressions.Regex.Replace(nombreCompleto, "[^a-zA-Z0-9]", "").ToUpper();
                if (nombreLimpio.Length > 10) nombreLimpio = nombreLimpio.Substring(0, 10);
            }

            string fecha = DateTime.Now.ToString("yyMMdd");
            string sufijo = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();

            return $"{nombreLimpio}-{fecha}-{sufijo}";
        }
        private async Task<bool> ValidarEventoBloqueado(int idEvento, NpgsqlConnection conexion, NpgsqlTransaction transaccion = null)
        {
            string sql = @"SELECT ""Bloqueado"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id";
            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("@id", idEvento);
                var res = await cmd.ExecuteScalarAsync();
                return res != null && res != DBNull.Value && (bool)res;
            }
        }
        /// <summary>
        /// Función centralizada para determinar si un usuario tiene acceso a un evento.
        /// Valida si es administrador, si ya está registrado, y reglas de privacidad alternativas (OR):
        /// (Whitelist O Grupos O Dependencias de eventos previos).
        /// </summary>
        private async Task<bool> ValidarAccesoAEvento(int idEvento, int idUsuario, NpgsqlConnection conexion, NpgsqlTransaction transaccion = null)
        {
            bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin) || User.TienePermiso(Modulo, PermisoEditar);
            // 1. Administradores y Editores tienen acceso total
            if (esAdmin) return true;

            // 2. CANDADO DE SEGURIDAD INVERSO: Si el usuario ya logró registrarse alguna vez en ESTE evento,
            // su acceso está garantizado de por vida para ver sus datos/pagos.
            string sqlYaRegistrado = @"SELECT COUNT(*) FROM ""Eventos_A_Registros"" WHERE ""Id_Evento"" = @ev AND ""Id_Usuario"" = @usr";
            using (var cmd = new NpgsqlCommand(sqlYaRegistrado, conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("@ev", idEvento);
                cmd.Parameters.AddWithValue("@usr", idUsuario);
                if ((long)await cmd.ExecuteScalarAsync() > 0) return true;
            }

            // 3. OBTENER CONFIGURACIÓN BÁSICA DE PRIVACIDAD
            bool esPrivadoDb = false;
            int? idGrupoUsuarios = null;

            string sqlEv = @"SELECT ""Es_Privado"", ""Id_Grupo_Usuarios"", ""Bloqueado"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @ev";
            using (var cmd = new NpgsqlCommand(sqlEv, conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("@ev", idEvento);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        esPrivadoDb = r["Es_Privado"] != DBNull.Value && (bool)r["Es_Privado"];
                        idGrupoUsuarios = r["Id_Grupo_Usuarios"] as int?;
                        ViewBag.EventoBloqueado = r["Bloqueado"] != DBNull.Value && (bool)r["Bloqueado"]; // <--- LÍNEA NUEVA
                    }
                    else return false;
                }
            }

            // 4. OBTENER CONFIGURACIÓN DE EVENTO PADRE
            bool tieneDependencia = false;
            var dependencias = new List<(int IdPadre, int? IdSubtipoPadre)>();

            string sqlDep = @"SELECT ""Id_Evento_Padre"", ""Id_Subtipo_Padre"" FROM ""Eventos_ValidacionPorEvento"" WHERE ""Id_Evento"" = @ev";
            using (var cmd = new NpgsqlCommand(sqlDep, conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("@ev", idEvento);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        tieneDependencia = true;
                        dependencias.Add(((int)r["Id_Evento_Padre"], r["Id_Subtipo_Padre"] as int?));
                    }
                }
            }

            // Si tiene un evento padre vinculado, se vuelve privado automáticamente para la lógica
            bool esPrivadoEfectivo = esPrivadoDb || tieneDependencia;

            // 5. EVALUAR REGLAS (LÓGICA 'OR')
            if (esPrivadoEfectivo)
            {
                // A. Validar Whitelist o Grupo (Si están configurados o el check está activo)
                string sqlPriv = @"
                    SELECT COUNT(*) 
                    FROM (
                        SELECT 1 FROM ""Eventos_Inscriptores"" WHERE ""Id_Evento""=@ev AND ""Id_Usuario""=@usr
                        UNION
                        SELECT 1 FROM ""Sist_Grupos_Miembros"" WHERE ""Id_Grupo""=@grupo AND ""Id_Usuario""=@usr
                    ) as permisos";

                using (var cmd = new NpgsqlCommand(sqlPriv, conexion, transaccion))
                {
                    cmd.Parameters.AddWithValue("@ev", idEvento);
                    cmd.Parameters.AddWithValue("@usr", idUsuario);
                    cmd.Parameters.AddWithValue("@grupo", (object)idGrupoUsuarios ?? DBNull.Value);

                    // ¡Tiene la llave de Whitelist/Grupo!
                    if ((long)await cmd.ExecuteScalarAsync() > 0) return true;
                }

                // B. Validar Evento Padre (Si no pasó la anterior, probamos esta llave)
                if (tieneDependencia)
                {
                    string sqlValidarPadre = @"
                        SELECT COUNT(*)
                        FROM ""Eventos_A_Registros"" r
                        JOIN ""Eventos_B_Asistentes"" b ON r.""Id_Registro"" = b.""Id_Registro""
                        WHERE r.""Id_Evento"" = @padre 
                          AND r.""Id_Usuario"" = @usr
                          AND (@sub IS NULL OR b.""Id_Subtipo"" = @sub)
                          AND (
                              b.""Es_Pagado"" = TRUE 
                              OR EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE)
                          )";

                    foreach (var dep in dependencias)
                    {
                        using (var cmd = new NpgsqlCommand(sqlValidarPadre, conexion, transaccion))
                        {
                            cmd.Parameters.AddWithValue("@padre", dep.IdPadre);
                            cmd.Parameters.AddWithValue("@usr", idUsuario);
                            cmd.Parameters.AddWithValue("@sub", (object)dep.IdSubtipoPadre ?? DBNull.Value);

                            // ¡Tiene la llave del Evento Padre pagado!
                            if ((long)await cmd.ExecuteScalarAsync() > 0) return true;
                        }
                    }
                }

                // Si intentamos todas las llaves y ninguna funcionó, se rechaza el acceso
                return false;
            }

            // 6. Si no es privado y no tiene dependencias, cualquiera puede entrar
            return true;
        }

        /// <summary>
        /// Muestra el calendario. Filtra eventos privados para que solo sean visibles por Admins o usuarios que cumplan los requisitos.
        /// Oculta eventos pasados por defecto, pero permite al admin alternar su visualización.
        /// </summary>
        [AllowAnonymous]
        public async Task<IActionResult> Index(bool mostrarPasados = false)
        {
            var modelo = new CalendarioViewModel();
            int idUser = 0;
            if (User.Identity.IsAuthenticated)
            {
                int.TryParse(User.FindFirst("IdUsuario")?.Value, out idUser);
                await VerificarPagosPendientes();
                await LimpiarExpiradosStripe();
            }

            bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);
            ViewBag.EsAdmin = esAdmin;
            bool verPasados = esAdmin && mostrarPasados;
            ViewBag.MostrarPasados = verPasados;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // SQL Simplificado: Ya no intentamos calcular permisos aquí, 
                    // solo traemos lo que es visible por fecha y estatus activo.
                    string sql = $@"
                SELECT e.*,
                    -- Mantener los conteos de montos y confirmados que son útiles para la vista
                    (SELECT COALESCE(COUNT(*), 0) FROM ""Eventos_B_Asistentes"" b JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" WHERE r.""Id_Evento"" = e.""Id_Evento"" AND r.""Id_Usuario"" = @uid AND b.""Es_Pagado"" = TRUE) as ""Confirmados"",
                    (SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) FROM ""Eventos_C_Cuentas_Cobrar"" c JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente"" JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" WHERE r.""Id_Evento"" = e.""Id_Evento"" AND r.""Id_Usuario"" = @uid AND c.""Pagado"" = FALSE AND cal.""Fecha_Limite"" <= (NOW() + INTERVAL '45 days')) as ""MontoVencido"",
                    (SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) FROM ""Eventos_C_Cuentas_Cobrar"" c JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente"" JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" WHERE r.""Id_Evento"" = e.""Id_Evento"" AND r.""Id_Usuario"" = @uid AND c.""Pagado"" = FALSE AND cal.""Fecha_Limite"" > (NOW() + INTERVAL '45 days')) as ""MontoFuturo"",
                    (SELECT COUNT(DISTINCT b.""Id_Asistente"") FROM ""Eventos_B_Asistentes"" b JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" JOIN ""Eventos_C_Cuentas_Cobrar"" c ON b.""Id_Asistente"" = c.""Id_Asistente"" WHERE r.""Id_Evento"" = e.""Id_Evento"" AND c.""Pagado"" = TRUE) as ""TotalGlobal""
                FROM ""Eventos_Catalogo"" e
                WHERE (@esAdmin = TRUE OR e.""Activo"" = TRUE)
                AND (@verPasados = TRUE OR COALESCE(e.""Fecha_Fin"", e.""Fecha_Inicio"") >= CURRENT_DATE)
                ORDER BY e.""Activo"" DESC, e.""Es_Destacado"" DESC, e.""Fecha_Inicio"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@esAdmin", esAdmin);
                        cmd.Parameters.AddWithValue("@verPasados", verPasados);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idEventoActual = (int)r["Id_Evento"];

                                // --- VALIDACIÓN UNIFICADA ---
                                // Abrimos una conexión temporal o usamos la actual para validar cada uno
                                using (var conValidar = new NpgsqlConnection(_cadenaConexion))
                                {
                                    await conValidar.OpenAsync();
                                    if (!await ValidarAccesoAEvento(idEventoActual, idUser, conValidar))
                                    {
                                        continue; // Si no tiene acceso, lo saltamos y no se agrega a la lista
                                    }
                                }

                                var item = new EventoItemViewModel
                                {
                                    Id_Evento = idEventoActual,
                                    Id_Encriptado_Evento = Funciones.EncriptarId(idEventoActual),
                                    Titulo = r["Titulo"].ToString(),
                                    Descripcion = r["Descripcion"].ToString(),
                                    Fecha_Inicio = (DateTime)r["Fecha_Inicio"],
                                    Fecha_Fin = r["Fecha_Fin"] == DBNull.Value ? (DateTime)r["Fecha_Inicio"] : (DateTime)r["Fecha_Fin"],
                                    Costo = (decimal)r["Costo_Entrada"],
                                    Requiere_Registro = (bool)r["Requiere_Registro"],
                                    Tipo_Registro = (int)r["Tipo_Registro"],
                                    Es_Destacado = (bool)r["Es_Destacado"],
                                    Permitir_Pago = (bool)r["Permitir_Pago"],
                                    Activo = (bool)r["Activo"],
                                    Imagen_Url = r["Imagen_Url"]?.ToString(),
                                    MiCantidadConfirmada = Convert.ToInt32(r["Confirmados"]),
                                    MiMontoDeuda = Convert.ToDecimal(r["MontoVencido"]),
                                    MiMontoFuturo = Convert.ToDecimal(r["MontoFuturo"]),
                                    TotalAsistentesGlobal = Convert.ToInt32(r["TotalGlobal"]),
                                    Cupo_Maximo = (int)r["Cupo_Maximo"],
                                    EsPrivado = (bool)r["Es_Privado"],
                                    Bloqueado = r["Bloqueado"] != DBNull.Value && (bool)r["Bloqueado"]
                                };

                                if (item.Activo && item.Es_Destacado && modelo.EventoDestacado == null)
                                    modelo.EventoDestacado = item;
                                else
                                    modelo.ListaEventos.Add(item);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        /// <summary>
        /// Muestra los detalles del evento. Valida acceso si es Privado y procesa la galería y subtipos.
        /// </summary>
        [AllowAnonymous]
        public async Task<IActionResult> Detalle(string sid)
        {
            // Validamos sesión para checks de seguridad
            int idUser = 0;
            bool esAdmin = false;
            if (User.Identity.IsAuthenticated)
            {
                idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                esAdmin = User.TienePermiso(Modulo, PermisoAdmin) || User.TienePermiso(Modulo, PermisoEditar);
                await VerificarPagosPendientes();
            }

            var modelo = new DetalleEventoViewModel();
            try
            {
                var id = Funciones.DesencriptarId(sid);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDACIÓN DE ACCESO CENTRALIZADA (Se hace primero por seguridad)
                    if (!await ValidarAccesoAEvento(id, idUser, conexion))
                    {
                        if (idUser == 0)
                        {
                            MostrarMensaje("Acceso Restringido", "Este evento requiere inicio de sesión para validar tus permisos de acceso.", TipoMensaje.Alerta);
                            return RedirectToAction("Index", "Login");
                        }

                        MostrarMensaje("Acceso Denegado", "No cumples con los requisitos necesarios (invitación, grupo o evento previo) para ver este evento.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    // 2. VALIDACIÓN Y GENERACIÓN DINÁMICA DE IMAGEN 
                    // Se ejecuta aquí para garantizar que la BD tenga la URL procesada más reciente si los cupos acaban de cambiar.
                    await ProcesarImagenConSellos(id);

                    // 3. CARGAR EVENTO Y URLS DE IMAGEN
                    string sql = @"SELECT * FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                bool activo = (bool)r["Activo"];

                                // SEGURIDAD: Si no está activo y no soy admin -> Fuera
                                if (!activo && !esAdmin)
                                {
                                    MostrarMensaje("Error", "No tienes permisos para visualizar éste evento", TipoMensaje.Alerta);
                                    return RedirectToAction("Index");
                                }

                                bool esPrivado = r["Es_Privado"] != DBNull.Value && (bool)r["Es_Privado"];

                                // Lógica de inyección de Imagen Dinámica
                                bool esDinamica = r["ImagenDinamica"] != DBNull.Value && (bool)r["ImagenDinamica"];
                                string urlBase = r["Imagen_Url"]?.ToString();
                                string urlDinamica = r["ImagenDinamica_Url"]?.ToString();

                                // Si está encendida la dinámica y existe URL procesada, la usamos; si no, la original.
                                string urlFinal = (esDinamica && !string.IsNullOrEmpty(urlDinamica)) ? urlDinamica : urlBase;

                                // Pasamos el flag a la vista para la lógica Anti-Caché de navegadores
                                ViewBag.EsImagenDinamica = esDinamica;

                                modelo.Evento = new EventoItemViewModel
                                {
                                    Id_Evento = (int)r["Id_Evento"],
                                    Id_Encriptado_Evento = Funciones.EncriptarId((int)r["Id_Evento"]),
                                    Titulo = r["Titulo"].ToString(),
                                    Descripcion = r["Descripcion"].ToString(),
                                    Fecha_Inicio = (DateTime)r["Fecha_Inicio"],
                                    Costo = (decimal)r["Costo_Entrada"],
                                    Requiere_Registro = (bool)r["Requiere_Registro"],
                                    Tipo_Registro = (int)r["Tipo_Registro"],
                                    Permitir_Pago = (bool)r["Permitir_Pago"],
                                    EsPrivado = esPrivado,
                                    Imagen_Url = NormalizarUrlImagen(urlFinal, ModoVisualizacionImagen.Incrustado),
                                    Imagen_Link_Preview = NormalizarUrlImagen(urlFinal, ModoVisualizacionImagen.VistaPrevia)
                                };

                                if (r["Id_Encuesta_Requisito"] != DBNull.Value)
                                {
                                    modelo.TieneEncuestaRequisito = true;
                                    modelo.IdEncuestaRequisito = (int)r["Id_Encuesta_Requisito"];
                                }
                            }
                            else
                            {
                                MostrarMensaje("Error", "El evento solicitado no existe o fue eliminado.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 3.1 CARGAR DATOS DE LA ENCUESTA (SI APLICA)
                    if (modelo.TieneEncuestaRequisito)
                    {
                        using (var cmdEnc = new NpgsqlCommand("SELECT \"Clave_Url\" FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\" = @id", conexion))
                        {
                            cmdEnc.Parameters.AddWithValue("@id", modelo.IdEncuestaRequisito);
                            var urlObj = await cmdEnc.ExecuteScalarAsync();
                            if (urlObj != null && urlObj != DBNull.Value)
                            {
                                modelo.UrlEncuestaRequisito = urlObj.ToString();
                            }
                        }

                        if (idUser > 0)
                        {
                            using (var cmdResp = new NpgsqlCommand("SELECT COUNT(1) FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Encuesta\" = @idEnc AND \"Id_Usuario\" = @idUsr", conexion))
                            {
                                cmdResp.Parameters.AddWithValue("@idEnc", modelo.IdEncuestaRequisito);
                                cmdResp.Parameters.AddWithValue("@idUsr", idUser);
                                long numResp = (long)await cmdResp.ExecuteScalarAsync();
                                modelo.EncuestaYaRespondida = (numResp > 0);
                            }
                        }
                    }

                    // 4. Total Asistentes y Carga de Subtipos unificada
                    var disponibilidad = await ConsultarDisponibilidadEvento(id, conexion);

                    if (disponibilidad != null)
                    {
                        if (User.Identity.IsAuthenticated && User.TienePermiso(Modulo, PermisoLeer))
                        {
                            modelo.TotalAsistentesGlobal = disponibilidad.OcupadosGlobal;
                        }

                        foreach (var s in disponibilidad.Modalidades)
                        {
                            modelo.Subtipos.Add(new SubtipoEventoItem
                            {
                                Id_Subtipo = s.IdSubtipo,
                                Nombre = s.NombreModalidad,
                                Costo = s.Costo,
                                Cupo = s.CupoTotal,
                                Ocupados = s.LugaresOcupados
                            });
                        }
                    }

                    // 5. CARGAR GALERÍA (Con procesamiento de URLs)
                    string sqlGal = @"SELECT * FROM ""Eventos_Galeria"" WHERE ""Id_Evento"" = @id ORDER BY ""Orden"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlGal, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string urlOriginal = r["Url_Foto"].ToString();
                                modelo.Galeria.Add(new FotoGaleriaItem
                                {
                                    Descripcion = r["Descripcion"]?.ToString(),
                                    Url_Foto = NormalizarUrlImagen(urlOriginal, ModoVisualizacionImagen.Incrustado),
                                    Url_Link_Preview = NormalizarUrlImagen(urlOriginal, ModoVisualizacionImagen.VistaPrevia)
                                });
                            }
                        }
                    }

                    // 6. PROCESAR Y CARGAR EVENTOS HIJOS (Banners)
                    string sqlIdsHijos = @"
                    SELECT DISTINCT ve.""Id_Evento""
                    FROM ""Eventos_ValidacionPorEvento"" ve
                    JOIN ""Eventos_Catalogo"" e ON ve.""Id_Evento"" = e.""Id_Evento""
                    WHERE ve.""Id_Evento_Padre"" = @id AND e.""Activo"" = TRUE";

                    var idsHijosBrutos = new List<int>();
                    using (var cmdIds = new NpgsqlCommand(sqlIdsHijos, conexion))
                    {
                        cmdIds.Parameters.AddWithValue("@id", id);
                        using (var rIds = await cmdIds.ExecuteReaderAsync())
                            while (await rIds.ReadAsync()) idsHijosBrutos.Add((int)rIds["Id_Evento"]);
                    }

                    var idsHijosPermitidos = new List<int>();
                    foreach (var idH in idsHijosBrutos)
                    {
                        // USAR SIEMPRE LA FUNCIÓN
                        if (await ValidarAccesoAEvento(idH, idUser, conexion))
                            idsHijosPermitidos.Add(idH);
                    }

                    // Invocamos la verificación de sellos para cada hijo
                    // Gracias al "Escudo de Rendimiento" que agregamos antes, esto no saturará el servidor
                    foreach (var idHijo in idsHijosPermitidos)
                    {
                        await ProcesarImagenConSellos(idHijo);
                    }

                    // Ahora cargamos la información final con las URLs actualizadas
                    string sqlHijosFinal = @"
    SELECT DISTINCT e.""Id_Evento"", e.""Titulo"", e.""Fecha_Inicio"", e.""Imagen_Url"", 
                    e.""ImagenDinamica"", e.""ImagenDinamica_Url""
    FROM ""Eventos_Catalogo"" e
    WHERE e.""Id_Evento"" = ANY(@ids)
    ORDER BY e.""Fecha_Inicio"" ASC";

                    var eventosHijos = new List<EventoItemViewModel>();
                    if (idsHijosPermitidos.Any())
                    {
                        using (var cmdH = new NpgsqlCommand(sqlHijosFinal, conexion))
                        {
                            cmdH.Parameters.AddWithValue("@ids", idsHijosPermitidos);
                            using (var r = await cmdH.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    bool esDinamicaHijo = r["ImagenDinamica"] != DBNull.Value && (bool)r["ImagenDinamica"];
                                    string urlBaseHijo = r["Imagen_Url"]?.ToString();
                                    string urlDinamicaHijo = r["ImagenDinamica_Url"]?.ToString();

                                    // Lógica de selección de imagen
                                    string urlFinalHijo = (esDinamicaHijo && !string.IsNullOrEmpty(urlDinamicaHijo))
                                                          ? urlDinamicaHijo
                                                          : urlBaseHijo;

                                    eventosHijos.Add(new EventoItemViewModel
                                    {
                                        Id_Evento = (int)r["Id_Evento"],
                                        Id_Encriptado_Evento = Funciones.EncriptarId((int)r["Id_Evento"]),
                                        Titulo = r["Titulo"].ToString(),
                                        Fecha_Inicio = (DateTime)r["Fecha_Inicio"],
                                        Imagen_Url = NormalizarUrlImagen(urlFinalHijo, ModoVisualizacionImagen.Incrustado)
                                    });
                                }
                            }
                        }
                    }
                    ViewBag.EventosHijos = eventosHijos;

                    if (idUser > 0)
                    {
                        // Evaluamos si el usuario tiene algún asistente registrado cuya modalidad
                        // coincida con las capacidades de algún alojamiento publicado y activo.
                        string sqlAlojamiento = @"
        SELECT COUNT(b.""Id_Asistente"")
        FROM ""Eventos_B_Asistentes"" b
        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
        LEFT JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON b.""Id_Subtipo"" = amd.""Id_Subtipo""
        JOIN ""Eventos_H_Alojamiento_Capacidades"" cap 
             ON (cap.""Id_Subtipo"" = b.""Id_Subtipo"" OR cap.""Id_Agrupacion"" = amd.""Id_Agrupacion"")
        JOIN ""Eventos_G_Alojamientos"" g ON cap.""Id_Alojamiento"" = g.""Id_Alojamiento""
        JOIN ""Eventos_K_Alojamientos_Publicados"" pub ON g.""Id_Alojamiento"" = pub.""Id_Alojamiento""
        JOIN ""Eventos_ConfigAlojamiento"" conf ON r.""Id_Evento"" = conf.""Id_Evento""
        WHERE r.""Id_Evento"" = @idEv 
          AND r.""Id_Usuario"" = @idUsr 
          AND pub.""Activo"" = TRUE 
          AND conf.""Portal_Activo"" = TRUE";

                        using (var cmdAloj = new NpgsqlCommand(sqlAlojamiento, conexion))
                        {
                            cmdAloj.Parameters.AddWithValue("@idEv", id);
                            cmdAloj.Parameters.AddWithValue("@idUsr", idUser);

                            long coincidencias = (long)await cmdAloj.ExecuteScalarAsync();
                            modelo.MostrarBotonAlojamiento = coincidencias > 0;
                        }
                    }
                    else
                    {
                        modelo.MostrarBotonAlojamiento = false;
                    }
                    using (var conLegales = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conLegales.OpenAsync();
                        (var urlTerminos, var urlPrivacidad) = await Funciones.ObtenerDocumentosLegalesEventosAsync(conLegales);
                        ViewBag.UrlTerminos = urlTerminos;
                        ViewBag.UrlPrivacidad = urlPrivacidad;
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> DescargarFicha(string sId)
        {
            try
            {
                // --- 1. OBTENCIÓN DE DATOS (Igual que siempre) ---
                EventoItemViewModel evento = null;
                var subtipos = new List<SubtipoEventoItem>();
                var galeria = new List<FotoGaleriaItem>();
                var Id = Funciones.DesencriptarId(sId);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // A. Evento
                    var cmd = new NpgsqlCommand(@"SELECT * FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion);
                    cmd.Parameters.AddWithValue("@id", Id);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            evento = new EventoItemViewModel
                            {
                                Id_Evento = Id, // Importante para enlaces
                                Id_Encriptado_Evento = sId, // Importante para enlaces
                                Titulo = r["Titulo"].ToString(),
                                Descripcion = r["Descripcion"].ToString(),
                                Fecha_Inicio = (DateTime)r["Fecha_Inicio"],
                                Costo = (decimal)r["Costo_Entrada"],
                                Cupo_Maximo = (int)r["Cupo_Maximo"],
                                Imagen_Url = NormalizarUrlImagen(r["Imagen_Url"]?.ToString(), ModoVisualizacionImagen.Incrustado),
                                EsPrivado = r["Es_Privado"] != DBNull.Value && (bool)r["Es_Privado"]
                            };
                        }
                        else return NotFound();
                    }

                    // B. Subtipos
                    var disponibilidad = await ConsultarDisponibilidadEvento(Id, conexion);
                    if (disponibilidad != null)
                    {
                        foreach (var s in disponibilidad.Modalidades)
                        {
                            subtipos.Add(new SubtipoEventoItem
                            {
                                Nombre = s.NombreModalidad,
                                Costo = s.Costo,
                                Cupo = s.CupoTotal,
                                Ocupados = s.LugaresOcupados // <- Aquí le pasamos los lugares ocupados a la vista
                            });
                        }
                    }

                    // C. Galería
                    var cmdGal = new NpgsqlCommand(@"SELECT * FROM ""Eventos_Galeria"" WHERE ""Id_Evento"" = @id ORDER BY ""Orden"" ASC", conexion);
                    cmdGal.Parameters.AddWithValue("@id", Id);
                    using (var r = await cmdGal.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            galeria.Add(new FotoGaleriaItem
                            {
                                Url_Foto = NormalizarUrlImagen(r["Url_Foto"]?.ToString(), ModoVisualizacionImagen.Incrustado),
                                Descripcion = r["Descripcion"]?.ToString()
                            });
                        }
                    }
                }

                // --- 2. PREPARAR MODELO PARA LA VISTA ---
                // Usaremos un ViewModel compuesto o ViewBag, aquí uso ViewBag para rápido,
                // pero idealmente crea una clase 'FichaEventoViewModel'.
                ViewBag.Evento = evento;
                ViewBag.Subtipos = subtipos;
                ViewBag.Galeria = galeria;

                // Retornamos la vista HTML
                return View("FichaEvento");
            }
            catch (Exception ex) { 
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error); 
                return RedirectToAction("Index", "Home");
            }
        }

        /// <summary>
        /// Muestra el historial de transferencias del usuario o invitado (según token).
        /// </summary>
        /// <param name="id">Es el Id del evento (opcional si viene por token).</param>
        /// <param name="token">Es el token de invitado (opcional).</param>
        /// <returns>Es la vista con el historial.</returns>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> MisTransferencias(string sid, string token = null)
        {
            var historial = new List<dynamic>();
            int idEvento = 0;
            bool esInvitado = !string.IsNullOrEmpty(token);

            try
            {
                // 1. Intentar obtener ID si viene en la URL
                if (!string.IsNullOrEmpty(sid))
                {
                    idEvento = Funciones.DesencriptarId(sid);
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = "";
                    var cmd = new NpgsqlCommand();
                    cmd.Connection = conexion;

                    if (esInvitado)
                    {
                        // ESCENARIO A: INVITADO

                        // Si no teníamos ID (porque no venía sid), lo buscamos por el token
                        if (idEvento == 0)
                        {
                            string sqlEv = @"SELECT r.""Id_Evento"" 
                                     FROM ""Eventos_B_Asistentes"" b 
                                     JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                     WHERE b.""Token_Pago_Externo"" = @tok LIMIT 1";
                            using (var cmdEv = new NpgsqlCommand(sqlEv, conexion))
                            {
                                cmdEv.Parameters.AddWithValue("@tok", token);
                                var res = await cmdEv.ExecuteScalarAsync();
                                if (res != null) idEvento = (int)res;
                            }
                        }

                        // Consulta de historial
                        sql = @"SELECT DISTINCT t.""Id_Transaccion"", t.""Monto_Total"", t.""Estatus_Pago"", 
                               t.""Fecha_Intento"", t.""Id_Archivo_Comprobante"", t.""Ref_Pasarela"", 
                               t.""Comentarios_Revision""
                        FROM ""Eventos_D_Transacciones"" t
                        JOIN ""Eventos_E_Pagos_Aplicados"" e ON t.""Id_Transaccion"" = e.""Id_Transaccion""
                        JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                        JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                        WHERE b.""Token_Pago_Externo"" = @param
                        AND (
                               t.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected') 
                               OR t.""Ref_Pasarela"" LIKE '%TRANSFERENCIA%' 
                        )
                        ORDER BY t.""Fecha_Intento"" DESC";

                        cmd.Parameters.AddWithValue("@param", token);
                    }
                    else if (User.Identity.IsAuthenticated)
                    {
                        // ESCENARIO B: INTERNO
                        int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

                        sql = @"SELECT t.""Id_Transaccion"", t.""Monto_Total"", t.""Estatus_Pago"", 
                               t.""Fecha_Intento"", t.""Id_Archivo_Comprobante"", t.""Ref_Pasarela"", 
                               t.""Comentarios_Revision""
                        FROM ""Eventos_D_Transacciones"" t
                        WHERE t.""Id_Usuario"" = @param AND t.""Id_Evento"" = @ev
                        AND (
                               t.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected') 
                               OR t.""Ref_Pasarela"" LIKE '%TRANSFERENCIA%' 
                        )
                        ORDER BY t.""Fecha_Intento"" DESC";

                        cmd.Parameters.AddWithValue("@param", idUsuario);
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                    }
                    else
                    {
                        return RedirectToAction("Index", "Login");
                    }

                    if (!string.IsNullOrEmpty(sql))
                    {
                        cmd.CommandText = sql;
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                historial.Add(new
                                {
                                    Id_Transaccion = (int)r["Id_Transaccion"],
                                    Monto_Total = (decimal)r["Monto_Total"],
                                    Estatus_Pago = r["Estatus_Pago"].ToString(),
                                    Fecha_Intento = (DateTime)r["Fecha_Intento"],
                                    Id_Archivo_Comprobante = r["Id_Archivo_Comprobante"] != DBNull.Value ? (int)r["Id_Archivo_Comprobante"] : 0,
                                    Comentario = r["Comentarios_Revision"] != DBNull.Value ? r["Comentarios_Revision"].ToString() : null
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            // --- CORRECCIÓN CRÍTICA AQUÍ ---
            // Si 'sid' vino vacío pero encontramos el evento (por token o lógica interna),
            // debemos calcular el hash ahora para que el botón "Volver" funcione.
            if (string.IsNullOrEmpty(sid) && idEvento > 0)
            {
                sid = Funciones.EncriptarId(idEvento);
            }

            ViewBag.IdEvento = idEvento;
            ViewBag.idEventoEncriptado = sid; // Ahora sí garantizamos que lleve valor
            ViewBag.Token = token;
            return View(historial);
        }

        /// <summary>
        /// Muestra el formulario de registro para un evento específico.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> Registro(string sid)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try { await VerificarPagosPendientes(); } catch { }

            try
            {
                var id = Funciones.DesencriptarId(sid);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    (var urlTerminos, var urlPrivacidad) = await Funciones.ObtenerDocumentosLegalesEventosAsync(conexion);
                    ViewBag.UrlTerminos = urlTerminos;
                    ViewBag.UrlPrivacidad = urlPrivacidad;

                    // 1. VALIDACIÓN DE ACCESO CENTRALIZADA
                    if (!await ValidarAccesoAEvento(id, idUser, conexion))
                    {
                        MostrarMensaje("Registro Restringido", "Lo sentimos, no tienes permitido el registro a este evento o no cumples con los requisitos previos necesarios.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    // 2. VALIDACIÓN DE ENCUESTA REQUISITO
                    int tipoRegistroValidacion = 1;
                    int idEncuestaRequisito = 0;
                    string urlEncuesta = "";

                    using (var cmdEv = new NpgsqlCommand("SELECT \"Tipo_Registro\", \"Id_Encuesta_Requisito\" FROM \"Eventos_Catalogo\" WHERE \"Id_Evento\" = @id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdEv.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                tipoRegistroValidacion = (int)r["Tipo_Registro"];
                                ViewBag.TipoRegistro = tipoRegistroValidacion;

                                if (r["Id_Encuesta_Requisito"] != DBNull.Value)
                                {
                                    idEncuestaRequisito = (int)r["Id_Encuesta_Requisito"];
                                }
                            }
                        }
                    }

                    if (idEncuestaRequisito > 0)
                    {
                        using (var cmdUrl = new NpgsqlCommand("SELECT \"Clave_Url\" FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\" = @idEnc", conexion))
                        {
                            cmdUrl.Parameters.AddWithValue("@idEnc", idEncuestaRequisito);
                            var resultUrl = await cmdUrl.ExecuteScalarAsync();
                            if (resultUrl != null && resultUrl != DBNull.Value)
                            {
                                urlEncuesta = resultUrl.ToString();
                            }
                        }
                    }

                    ViewBag.TieneEncuestaRequisito = idEncuestaRequisito > 0;
                    ViewBag.UrlEncuesta = urlEncuesta;
                    ViewBag.IdEncuestaRequisito = idEncuestaRequisito;

                    // 3. DATOS EVENTO Y SUBTIPOS UNIFICADOS
                    var disponibilidad = await ConsultarDisponibilidadEvento(id, conexion);

                    if (disponibilidad != null)
                    {
                        ViewBag.Id_Evento = id;
                        ViewBag.Id_Evento_Encriptado = sid;
                        ViewBag.TituloEvento = disponibilidad.Titulo;
                        ViewBag.CostoUnitario = disponibilidad.CostoEntrada;
                        ViewBag.CupoMaximo = disponibilidad.CupoMaximoEvento;
                        ViewBag.TotalOcupados = disponibilidad.OcupadosGlobal;
                        ViewBag.Permitir_Pago = disponibilidad.PermitirPago;

                        // Conservamos la consulta del Tipo_Registro (Ya consultado y guardado en ViewBag.TipoRegistro)

                        // 4. SUBTIPOS
                        var subtipos = new List<SubtipoEventoItem>();
                        foreach (var s in disponibilidad.Modalidades)
                        {
                            subtipos.Add(new SubtipoEventoItem
                            {
                                Id_Subtipo = s.IdSubtipo,
                                Nombre = s.NombreModalidad,
                                Costo = s.Costo,
                                Cupo = s.CupoTotal,
                                Ocupados = s.LugaresOcupados
                            });
                        }
                        ViewBag.Subtipos = subtipos;

                        // 4.5 PRODUCTOS EXTRA
                        var extras = new List<ProductoExtraItem>();
                        using (var cmdE = new NpgsqlCommand(@"SELECT * FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_Evento""=@id AND ""Activo""=TRUE ORDER BY ""Id_ProductoExtra""", conexion))
                        {
                            cmdE.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdE.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    extras.Add(new ProductoExtraItem
                                    {
                                        Id_ProductoExtra = (int)r["Id_ProductoExtra"],
                                        Id_Subtipo = r["Id_Subtipo"] as int?,
                                        Nombre_Producto = r["Nombre_Producto"].ToString(),
                                        Descripcion = r["Descripcion"]?.ToString(),
                                        Precio = (decimal)r["Precio"],
                                        Cantidad_Minima = (int)r["Cantidad_Minima"],
                                        Cantidad_Maxima = (int)r["Cantidad_Maxima"],
                                        Activo = (bool)r["Activo"]
                                    });
                                }
                            }
                        }
                        ViewBag.ProductosExtra = extras;
                    }
                    else
                    {
                        MostrarMensaje("Error", "No se pudo cargar la información del evento o no existe.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    // 3. PREGUNTAS
                    var preguntasDef = new List<PreguntaDefinicion>();
                    using (var cmd = new NpgsqlCommand(@"SELECT * FROM ""Eventos_Preguntas"" WHERE ""Id_Evento""=@id ORDER BY ""Id_Pregunta""", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync())
                        {
                            var p = new PreguntaDefinicion { Id_Pregunta = (int)r["Id_Pregunta"], Texto = r["Texto_Pregunta"].ToString(), Tipo = (TipoPregunta)(int)r["Tipo_Pregunta"] };
                            if (r["Opciones"] != DBNull.Value) p.Opciones = r["Opciones"].ToString().Split('|').ToList();
                            preguntasDef.Add(p);
                        }
                    }
                    ViewBag.PreguntasDefinidas = preguntasDef;

                    // 5. HISTORIAL 
                    var registrados = new List<AsistenteRegistrado>();
                    string sqlHist = @"
                    SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Etiqueta_Grupo"", b.""Edad"", b.""Genero"", b.""Es_Pagado"", b.""Id_Subtipo"",
                           b.""Token_Pago_Externo"",
                           COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                                     WHERE h.""Id_Encuesta"" = @idEncReq 
                                       AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida"",
                           (SELECT COALESCE(SUM(""Total""), 0) FROM ""Eventos_Asistentes_ProductosExtra"" WHERE ""Id_Asistente"" = b.""Id_Asistente"") as ""TotalExtras"",
                           s.""Nombre"" as ""NomSub"", s.""Costo"" as ""CostoSub"",
                           (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as ""PagosHechos"",
                           (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as ""TotalPagos"",
                           (SELECT d.""Id_Transaccion"" FROM ""Eventos_D_Transacciones"" d JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" WHERE c.""Id_Asistente""=b.""Id_Asistente"" AND d.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected') LIMIT 1) as ""IdTrxPendiente"",
                           (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE AND cal.""Fecha_Limite"" < NOW()) as ""CantVencidos"",
                           (SELECT d.""Estatus_Pago"" FROM ""Eventos_D_Transacciones"" d JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" WHERE c.""Id_Asistente""=b.""Id_Asistente"" AND d.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected') LIMIT 1) as ""StTrx"",
                           (SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" a WHERE a.""Id_Asistente"" = b.""Id_Asistente"") as ""AsignacionesAlojamientos"",
                           (
                                SELECT COALESCE(MAX(EXTRACT(EPOCH FROM (T.Base + INTERVAL '30 minutes' - NOW()))), 0)
                                FROM (
                                    SELECT r2.""Fecha_Registro"" as Base
                                    FROM ""Eventos_A_Registros"" r2 
                                    WHERE r2.""Id_Registro"" = b.""Id_Registro"" AND b.""Es_Pagado"" = FALSE
                                    UNION ALL
                                    SELECT t.""Fecha_Intento"" as Base
                                    FROM ""Eventos_D_Transacciones"" t
                                    JOIN ""Eventos_E_Pagos_Aplicados"" pa ON t.""Id_Transaccion"" = pa.""Id_Transaccion""
                                    JOIN ""Eventos_C_Cuentas_Cobrar"" cc ON pa.""Id_Cuenta"" = cc.""Id_Cuenta""
                                    WHERE cc.""Id_Asistente"" = b.""Id_Asistente"" AND t.""Estatus_Pago"" = 'pending' AND t.""Completado"" = FALSE
                                ) T
                           ) as ""SegundosRestantes""
                    FROM ""Eventos_B_Asistentes"" b 
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro""=r.""Id_Registro""
                    LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo""=s.""Id_Subtipo""
                    WHERE r.""Id_Evento""=@id AND r.""Id_Usuario""=@uid 
                    ORDER BY b.""Es_Pagado"" ASC, b.""Nombre_Completo"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlHist, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@idEncReq", idEncuestaRequisito);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                // Parseamos y limitamos los segundos matemáticos aquí en C#
                                int segs = r["SegundosRestantes"] != DBNull.Value ? Convert.ToInt32(Convert.ToDouble(r["SegundosRestantes"])) : 0;
                                if (segs > 1800) segs = 1800;
                                if (segs < 0) segs = 0;

                                var a = new AsistenteRegistrado
                                {
                                    Id_Asistente = (int)r["Id_Asistente"],
                                    NombreCompleto = r["Nombre_Completo"].ToString(),
                                    EtiquetaGrupo = r["Etiqueta_Grupo"]?.ToString() ?? "General",
                                    Edad = (int)r["Edad"],
                                    Genero = r["Genero"].ToString(),
                                    EsPagado = (bool)r["Es_Pagado"],
                                    IdSubtipo = r["Id_Subtipo"] as int?,
                                    NombreSubtipo = r["NomSub"]?.ToString(),
                                    EstatusTransaccion = r["StTrx"]?.ToString(),
                                    IdTransaccionPendiente = r["IdTrxPendiente"] != DBNull.Value ? (int)r["IdTrxPendiente"] : 0,
                                    TokenExterno = r["Token_Pago_Externo"] != DBNull.Value ? r["Token_Pago_Externo"].ToString() : null,
                                    EncuestaRespondida = idEncuestaRequisito > 0 ? (bool)r["EncuestaRespondida"] : true,

                                    PagosRealizados = Convert.ToInt32(r["PagosHechos"]),
                                    TotalPagosCalendario = Convert.ToInt32(r["TotalPagos"]),
                                    TieneVencidos = Convert.ToInt32(r["CantVencidos"]) > 0,
                                    CostoBaseModalidad = (r["CostoSub"] != DBNull.Value ? (decimal)r["CostoSub"] : (decimal)ViewBag.CostoUnitario),
                                    CostoExtras = r["TotalExtras"] != DBNull.Value ? (decimal)r["TotalExtras"] : 0m,
                                    CostoReal = (r["CostoSub"] != DBNull.Value ? (decimal)r["CostoSub"] : (decimal)ViewBag.CostoUnitario) + (r["TotalExtras"] != DBNull.Value ? (decimal)r["TotalExtras"] : 0m),
                                    EstaAsignadoAlojamiento = Convert.ToInt32(r["AsignacionesAlojamientos"]) > 0,
                                    SegundosRestantesApartado = segs 
                                };
                                a.EstaBloqueado = !string.IsNullOrEmpty(a.EstatusTransaccion);
                                registrados.Add(a);
                            }
                        }
                    }

                    // Cargar Respuestas y Productos Extra comprados
                    foreach (var a in registrados)
                    {
                        using (var cmd = new NpgsqlCommand("SELECT * FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\"=@id", conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", a.Id_Asistente);
                            using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) a.Respuestas.Add(new RespuestaItem { Id_Pregunta = (int)r["Id_Pregunta"], Valor = r["Valor_Respuesta"].ToString() });
                        }

                        string sqlPE = @"
                        SELECT pe.""Id_ProductoExtra"", cat.""Nombre_Producto"", pe.""Cantidad"", pe.""Precio_Unitario"", pe.""Total"",
                               cat.""Cantidad_Minima"", cat.""Cantidad_Maxima"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                WHERE c.""Id_Asistente"" = pe.""Id_Asistente"" 
                                  AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"") 
                                  AND c.""Pagado"" = TRUE) as ""CantPagada"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                WHERE c.""Id_Asistente"" = pe.""Id_Asistente"" 
                                  AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"") 
                                  AND c.""Pagado"" = FALSE) as ""CantPendiente""
                        FROM ""Eventos_Asistentes_ProductosExtra"" pe
                        JOIN ""Eventos_Catalogo_ProductosExtra"" cat ON pe.""Id_ProductoExtra"" = cat.""Id_ProductoExtra""
                        WHERE pe.""Id_Asistente"" = @id";

                        using (var cmdPE = new NpgsqlCommand(sqlPE, conexion))
                        {
                            cmdPE.Parameters.AddWithValue("@id", a.Id_Asistente);
                            using (var rPE = await cmdPE.ExecuteReaderAsync())
                            {
                                while (await rPE.ReadAsync())
                                {
                                    a.ProductosExtra.Add(new DetalleProductoExtraAsistente
                                    {
                                        Id_ProductoExtra = (int)rPE["Id_ProductoExtra"],
                                        NombreProducto = rPE["Nombre_Producto"].ToString(),
                                        PrecioUnitario = (decimal)rPE["Precio_Unitario"],
                                        CantidadComprada = (int)rPE["Cantidad"],
                                        Total = (decimal)rPE["Total"],
                                        CantidadPagada = Convert.ToInt32(rPE["CantPagada"]),
                                        CantidadPendiente = Convert.ToInt32(rPE["CantPendiente"]),
                                        CantidadMinima = (int)rPE["Cantidad_Minima"],
                                        CantidadMaxima = (int)rPE["Cantidad_Maxima"]
                                    });
                                }
                            }
                        }
                    }
                    ViewBag.Registrados = registrados;

                    bool tieneMapa = false;
                    using (var cmdMapa = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Eventos_Modalidades_Grafico"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdMapa.Parameters.AddWithValue("@id", id);
                        tieneMapa = Convert.ToInt64(await cmdMapa.ExecuteScalarAsync()) > 0;
                    }
                    ViewBag.TieneMapa = tieneMapa;

                    // 6. PROPUESTA
                    var propuesta = new List<AsistenteItem>();
                    if (registrados.Count == 0 && ((int)ViewBag.TotalOcupados < (int)ViewBag.CupoMaximo))
                    {
                        string nom = User.Identity.Name; int ed = 18; string gen = "H";
                        try
                        {
                            using (var cmd = new NpgsqlCommand("SELECT \"NombreCompleto\", \"Fecha_Nacimiento\", \"Genero\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\"=@u", conexion))
                            {
                                cmd.Parameters.AddWithValue("@u", idUser);
                                using (var r = await cmd.ExecuteReaderAsync()) if (await r.ReadAsync())
                                {
                                    if (r[0] != DBNull.Value) nom = r[0].ToString();
                                    if (r[1] != DBNull.Value) { var d = (DateTime)r[1]; ed = DateTime.Today.Year - d.Year; if (DateTime.Today < d.AddYears(ed)) ed--; }
                                    if (r[2] != DBNull.Value) gen = r[2].ToString().ToUpper().StartsWith("M") ? "M" : "H";
                                }
                            }
                        }
                        catch { }
                        var yo = new AsistenteItem { NombreCompleto = nom, Edad = ed, Genero = gen };
                        foreach (var pd in preguntasDef) yo.Respuestas.Add(new RespuestaItem { Id_Pregunta = pd.Id_Pregunta });
                        propuesta.Add(yo);
                    }
                    ViewBag.Asistentes = propuesta;
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View();
        }

        /// <summary>
        /// Es el método que procesa el registro de asistentes a un evento.
        /// </summary>
        /// <param name="modelo">Es el modelo con los datos del registro.</param>
        /// <returns>Retorna a la vista correspondiente.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarRegistro(RegistroEventoViewModel modelo)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string _sid = Funciones.EncriptarId(modelo.Id_Evento);
            if (modelo.Asistentes == null || !modelo.Asistentes.Any())
            {
                MostrarMensaje("Error", "No haz agregado ningún asistente", TipoMensaje.Alerta);
                return RedirectToAction("Registro", new { sid = _sid });
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (await ValidarEventoBloqueado(modelo.Id_Evento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "Por logística, el evento se encuentra bloqueado y ya no permite modificaciones a los registros.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = _sid });
                    }

                    if (!await ValidarAccesoAEvento(modelo.Id_Evento, idUser, conexion))
                    {
                        MostrarMensaje("Acceso Denegado", "No cumples con los requisitos para registrarte en este evento.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int cantidadNuevos = modelo.Asistentes.Count(x => x.Id_Asistente == 0);

                            var disp = await ConsultarDisponibilidadEvento(modelo.Id_Evento, conexion, trans, forUpdate: true);

                            if (disp == null)
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("Error", "El evento al que intentas registrarte ya no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }

                            if (!disp.EventoActivo)
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("Evento Cerrado", "Lo sentimos, este evento ya no está activo o ha finalizado.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }

                            if (cantidadNuevos > 0)
                            {
                                // Si el evento tiene modalidades, se desactiva el cupo global y se valida por subtipo.
                                if (disp.Modalidades != null && disp.Modalidades.Any())
                                {
                                    // Agrupamos y contamos cuántos nuevos asistentes se están intentando meter por cada modalidad en esta petición
                                    var cantidadPorModalidad = modelo.Asistentes
                                        .Where(x => x.Id_Asistente == 0 && x.Id_Subtipo_Seleccionado.HasValue)
                                        .GroupBy(x => x.Id_Subtipo_Seleccionado.Value)
                                        .ToDictionary(g => g.Key, g => g.Count());

                                    foreach (var kvp in cantidadPorModalidad)
                                    {
                                        var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == kvp.Key);
                                        if (modal == null)
                                        {
                                            await trans.RollbackAsync();
                                            MostrarMensaje("Error", "Modalidad inválida seleccionada.", TipoMensaje.Error);
                                            return RedirectToAction("Registro", new { sid = _sid });
                                        }

                                        if ((modal.LugaresOcupados + kvp.Value) > modal.CupoTotal)
                                        {
                                            await trans.RollbackAsync();
                                            string msg = modal.LugaresDisponibles <= 0
                                                ? $"¡Uy! Alguien más ocupó los últimos lugares de la modalidad '{modal.NombreModalidad}' mientras llenabas el formulario."
                                                : $"Solo quedan {modal.LugaresDisponibles} lugares disponibles para la modalidad '{modal.NombreModalidad}' y estás intentando registrar {kvp.Value}.";

                                            MostrarMensaje("Cupo Excedido", msg, TipoMensaje.Error);
                                            return RedirectToAction("Registro", new { sid = _sid });
                                        }
                                    }
                                }
                                else
                                {
                                    // Si NO tiene modalidades, el cupo global opera normalmente
                                    if ((disp.OcupadosGlobal + cantidadNuevos) > disp.CupoMaximoEvento)
                                    {
                                        await trans.RollbackAsync();
                                        string msg = disp.DisponiblesGlobal <= 0
                                            ? "¡Uy! Alguien más acaba de tomar el último lugar disponible mientras llenabas el formulario."
                                            : $"Alguien más reservó lugares. Solo quedan {disp.DisponiblesGlobal} lugares disponibles y estás intentando registrar {cantidadNuevos}.";

                                        MostrarMensaje("Cupo Excedido", msg, TipoMensaje.Error);
                                        return RedirectToAction("Registro", new { sid = _sid });
                                    }
                                }
                            }

                            bool bDebeElegirSubtipo = false;
                            var sQuery = @"SELECT COUNT(*) FROM ""Eventos_Subtipos"" WHERE ""Id_Evento""=@id AND ""Activo""=TRUE";
                            using (var cmd = new NpgsqlCommand(sQuery, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", modelo.Id_Evento);
                                var resultado = await cmd.ExecuteScalarAsync();
                                bDebeElegirSubtipo = resultado != null && Convert.ToInt32(resultado) > 0;
                            }

                            if (bDebeElegirSubtipo)
                            {
                                foreach (var asis in modelo.Asistentes)
                                {
                                    if (!asis.Id_Subtipo_Seleccionado.HasValue || asis.Id_Subtipo_Seleccionado == 0)
                                    {
                                        string nombre = string.IsNullOrWhiteSpace(asis.NombreCompleto) ? "Un asistente" : asis.NombreCompleto;
                                        MostrarMensaje("Faltan Datos", $"Para continuar, '{nombre}' debe elegir una modalidad.", TipoMensaje.Error);
                                        await trans.RollbackAsync();
                                        return RedirectToAction("Registro", new { sid = _sid });
                                    }
                                }
                            }

                            var preguntasRequeridas = new Dictionary<int, string>();
                            string sqlReq = @"SELECT ""Id_Pregunta"", ""Texto_Pregunta"" FROM ""Eventos_Preguntas"" WHERE ""Id_Evento"" = @id";
                            using (var cmdReq = new NpgsqlCommand(sqlReq, conexion, trans))
                            {
                                cmdReq.Parameters.AddWithValue("@id", modelo.Id_Evento);
                                using (var r = await cmdReq.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync()) preguntasRequeridas.Add((int)r["Id_Pregunta"], r["Texto_Pregunta"].ToString());
                                }
                            }

                            if (preguntasRequeridas.Any())
                            {
                                foreach (var asis in modelo.Asistentes)
                                {
                                    foreach (var preg in preguntasRequeridas)
                                    {
                                        var respuesta = asis.Respuestas?.FirstOrDefault(x => x.Id_Pregunta == preg.Key);
                                        if (respuesta == null || string.IsNullOrWhiteSpace(respuesta.Valor))
                                        {
                                            string nombre = string.IsNullOrWhiteSpace(asis.NombreCompleto) ? "Un asistente" : asis.NombreCompleto;
                                            MostrarMensaje("Faltan Datos", $"Para continuar, '{nombre}' debe responder: {preg.Value}", TipoMensaje.Error);
                                            await trans.RollbackAsync();
                                            return RedirectToAction("Registro", new { sid = _sid });
                                        }
                                    }
                                }
                            }

                            int idRegistro = 0;

                            string sqlBusqueda = @"
                                SELECT ""Id_Registro"" 
                                FROM ""Eventos_A_Registros"" 
                                WHERE ""Id_Evento""=@ev AND ""Id_Usuario""=@u 
                                AND ""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes'
                                ORDER BY ""Id_Registro"" DESC LIMIT 1";

                            using (var cmd = new NpgsqlCommand(sqlBusqueda, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@ev", modelo.Id_Evento);
                                cmd.Parameters.AddWithValue("@u", idUser);
                                var res = await cmd.ExecuteScalarAsync();
                                if (res != null) idRegistro = (int)res;
                            }

                            if (idRegistro == 0)
                            {
                                using (var cmdI = new NpgsqlCommand("INSERT INTO \"Eventos_A_Registros\" (\"Id_Evento\", \"Id_Usuario\", \"Fecha_Registro\") VALUES (@ev, @u, NOW()) RETURNING \"Id_Registro\"", conexion, trans))
                                {
                                    cmdI.Parameters.AddWithValue("@ev", modelo.Id_Evento);
                                    cmdI.Parameters.AddWithValue("@u", idUser);
                                    idRegistro = (int)await cmdI.ExecuteScalarAsync();
                                }
                            }

                            decimal costoBaseEvento = 0;
                            using (var cmd = new NpgsqlCommand(@"SELECT ""Costo_Entrada"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento""=@id", conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", modelo.Id_Evento);
                                costoBaseEvento = (decimal)await cmd.ExecuteScalarAsync();
                            }

                            foreach (var asis in modelo.Asistentes)
                            {
                                decimal costoFinal = costoBaseEvento;
                                object idSubtipoDB = DBNull.Value;
                                bool esSubtipoLleno = false;

                                if (asis.Id_Subtipo_Seleccionado.HasValue && asis.Id_Subtipo_Seleccionado > 0)
                                {
                                    var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == asis.Id_Subtipo_Seleccionado);
                                    if (modal != null)
                                    {
                                        esSubtipoLleno = modal.LugaresOcupados >= modal.CupoTotal;
                                        if (asis.Id_Asistente == 0 && esSubtipoLleno) throw new Exception("La modalidad seleccionada se ha agotado.");
                                        if (asis.Id_Asistente == 0 && modal != null) modal.LugaresOcupados++;
                                        costoFinal = modal.Costo;
                                        idSubtipoDB = modal.IdSubtipo;
                                    }
                                    else
                                    {
                                        throw new Exception("Modalidad inválida.");
                                    }
                                }

                                int idAsis = asis.Id_Asistente;

                                if (idAsis == 0)
                                {
                                    // Protección contra doble post (idempotencia por registro)
                                    string checkDoble = @"SELECT ""Id_Asistente"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Registro"" = @reg AND ""Nombre_Completo"" = @nom";
                                    using (var cmdChk = new NpgsqlCommand(checkDoble, conexion, trans))
                                    {
                                        cmdChk.Parameters.AddWithValue("@reg", idRegistro);
                                        cmdChk.Parameters.AddWithValue("@nom", asis.NombreCompleto);
                                        var existeAsis = await cmdChk.ExecuteScalarAsync();
                                        if (existeAsis != null)
                                        {
                                            // Ya existe, saltamos la inserción para prevenir duplicación
                                            continue;
                                        }
                                    }

                                    string token = GenerarTokenAmigable(asis.NombreCompleto);
                                    asis.TokenExterno = token;
                                    string sqlIn = @"INSERT INTO ""Eventos_B_Asistentes"" (""Id_Registro"", ""Nombre_Completo"", ""Etiqueta_Grupo"", ""Token_Pago_Externo"", ""Edad"", ""Genero"", ""Id_Subtipo"", ""Es_Pagado"") 
                                                     VALUES (@reg, @nom, @grupo, @token, @ed, @gen, @sub, FALSE) RETURNING ""Id_Asistente""";
                                    using (var cmd = new NpgsqlCommand(sqlIn, conexion, trans))
                                    {
                                        SetParamsAsistente(cmd, idRegistro, asis, idSubtipoDB);
                                        idAsis = (int)await cmd.ExecuteScalarAsync();
                                    }

                                    // GENERAR DEUDA
                                    // La condición clave es: ( (""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo"" = @sub) )
                                    string sqlDeuda = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"")
                                                        SELECT @idA, ""Id_Calendario"", 
                                                        CASE WHEN (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) > 0 THEN
                                                            ROUND(
                                                                @costo * ( CAST(""Monto_Base"" AS DECIMAL) / (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) )
                                                            , 2)
                                                        ELSE @costo END, 
                                                        FALSE
                                                        FROM ""Eventos_Calendario"" 
                                                        WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))
                                                        ORDER BY ""Numero_Pago"" ASC";

                                    using (var cmd = new NpgsqlCommand(sqlDeuda, conexion, trans))
                                    {
                                        cmd.Parameters.AddWithValue("@idA", idAsis);
                                        cmd.Parameters.AddWithValue("@ev", modelo.Id_Evento);
                                        cmd.Parameters.AddWithValue("@costo", costoFinal);
                                        var paramSub = new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer);
                                        paramSub.Value = idSubtipoDB ?? DBNull.Value;
                                        cmd.Parameters.Add(paramSub);
                                        int generadas = await cmd.ExecuteNonQueryAsync();

                                        if (generadas == 0)
                                        {
                                            // Fallback: Pago único manual
                                            await new NpgsqlCommand($"INSERT INTO \"Eventos_C_Cuentas_Cobrar\" (\"Id_Asistente\", \"Monto_Pagar\",\"Id_Calendario\", \"Pagado\") VALUES ({idAsis}, {costoFinal}, NULL, FALSE)", conexion, trans).ExecuteNonQueryAsync();
                                        }
                                    }

                                    // INSERTAR PRODUCTOS EXTRA (NUEVO ASISTENTE)
                                    if (asis.ProductosExtra != null && asis.ProductosExtra.Any(x => x.Cantidad > 0))
                                    {
                                        foreach (var pe in asis.ProductosExtra.Where(x => x.Cantidad > 0))
                                        {
                                            decimal precioExtra = 0;
                                            using (var cmdP = new NpgsqlCommand(@"SELECT ""Precio"" FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_ProductoExtra"" = @idP", conexion, trans)) {
                                                cmdP.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                precioExtra = (decimal)(await cmdP.ExecuteScalarAsync() ?? 0m);
                                            }

                                            string sqlExtra = @"INSERT INTO ""Eventos_Asistentes_ProductosExtra"" (""Id_Asistente"", ""Id_ProductoExtra"", ""Cantidad"", ""Precio_Unitario"", ""Total"") VALUES (@idA, @idP, @cant, @precio, @total)";
                                            using (var cmdPE = new NpgsqlCommand(sqlExtra, conexion, trans))
                                            {
                                                cmdPE.Parameters.AddWithValue("@idA", idAsis);
                                                cmdPE.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                cmdPE.Parameters.AddWithValue("@cant", pe.Cantidad);
                                                cmdPE.Parameters.AddWithValue("@precio", precioExtra);
                                                cmdPE.Parameters.AddWithValue("@total", precioExtra * pe.Cantidad);
                                                await cmdPE.ExecuteNonQueryAsync();
                                            }

                                            int idCalExtra = 0;
                                            using (var cmdFind = new NpgsqlCommand(@"SELECT ""Id_Calendario"" FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @idEv AND ""Numero_Pago"" = -(10000 + @idP) LIMIT 1", conexion, trans))
                                            {
                                                cmdFind.Parameters.AddWithValue("@idEv", modelo.Id_Evento);
                                                cmdFind.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                var existing = await cmdFind.ExecuteScalarAsync();
                                                if (existing != null && existing != DBNull.Value) idCalExtra = Convert.ToInt32(existing);
                                            }

                                            if (idCalExtra == 0)
                                            {
                                                string sqlFix = @"INSERT INTO ""Eventos_Calendario"" (""Id_Evento"", ""Numero_Pago"", ""Nombre_Concepto"", ""Monto_Base"", ""Fecha_Limite"") 
                                                                  VALUES (
                                                                      @idEv, 
                                                                      -(10000 + @idP), 
                                                                      COALESCE((SELECT ""Nombre_Producto"" FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_ProductoExtra""=@idP), 'Producto Extra'), 
                                                                      @precio, 
                                                                      COALESCE((SELECT MAX(""Fecha_Limite"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @idEv AND ""Numero_Pago"" > 0), NOW() + INTERVAL '30 days')
                                                                  ) 
                                                                  RETURNING ""Id_Calendario""";
                                                using (var cmdFix = new NpgsqlCommand(sqlFix, conexion, trans))
                                                {
                                                    cmdFix.Parameters.AddWithValue("@idEv", modelo.Id_Evento);
                                                    cmdFix.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                    cmdFix.Parameters.AddWithValue("@precio", precioExtra);
                                                    idCalExtra = Convert.ToInt32(await cmdFix.ExecuteScalarAsync());
                                                }
                                            }

                                            for (int i = 0; i < pe.Cantidad; i++)
                                            {
                                                string sqlDeudaPE = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"") VALUES (@idA, @idCal, @precio, FALSE)";
                                                using (var cmdDPE = new NpgsqlCommand(sqlDeudaPE, conexion, trans))
                                                {
                                                    cmdDPE.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdDPE.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    cmdDPE.Parameters.AddWithValue("@precio", precioExtra);
                                                    await cmdDPE.ExecuteNonQueryAsync();
                                                }
                                            }
                                            costoFinal += (precioExtra * pe.Cantidad);
                                        }
                                    }
                                }
                                else
                                {
                                    // 1. Validar Permisos y Cargar Estado Actual
                                    string sqlEstado = @"SELECT b.""Id_Subtipo"", 
(SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente""=@idA AND ""Pagado""=TRUE) as ""Pagados"", 
(SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
    JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" 
    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" 
    WHERE c.""Id_Asistente""=@idA AND d.""Estatus_Pago"" IN ('waiting_proof', 'review')) as ""EnTramite"",
(SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
    JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" 
    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" 
    WHERE c.""Id_Asistente""=@idA AND d.""Estatus_Pago"" = 'pending' AND d.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes') as ""PendientesRecientes"",
(SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente""=@idA) as ""TotalCuentas"" 
FROM ""Eventos_B_Asistentes"" b 
JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
WHERE b.""Id_Asistente""=@idA AND r.""Id_Usuario""=@uid";

                                    int? dbSubtipo = null;
                                    long dbPagados = 0;
                                    long dbTramite = 0;
                                    long dbPendientesRecientes = 0;
                                    long dbTotalCuentas = 0;

                                    using (var cmdSt = new NpgsqlCommand(sqlEstado, conexion, trans))
                                    {
                                        cmdSt.Parameters.AddWithValue("@idA", idAsis);
                                        cmdSt.Parameters.AddWithValue("@uid", idUser);
                                        using (var r = await cmdSt.ExecuteReaderAsync())
                                        {
                                            if (await r.ReadAsync())
                                            {
                                                dbSubtipo = r["Id_Subtipo"] as int?;
                                                dbPagados = (long)r["Pagados"];
                                                dbTramite = (long)r["EnTramite"];
                                                dbPendientesRecientes = (long)r["PendientesRecientes"];
                                                dbTotalCuentas = (long)r["TotalCuentas"];
                                            }
                                            else throw new Exception("No tienes permiso para editar este asistente.");
                                        }
                                    }

                                    // CANDADO DE SEGURIDAD Y REGLAS DE NEGOCIO
                                    int nuevoSubtipo = asis.Id_Subtipo_Seleccionado ?? 0;
                                    int viejoSubtipo = dbSubtipo ?? 0;
                                    bool cambioDePlan = nuevoSubtipo != viejoSubtipo;

                                    if (cambioDePlan)
                                    {
                                        if (esSubtipoLleno)
                                            throw new Exception("No puedes cambiarte a esta modalidad porque ya está llena.");

                                        if (dbPagados > 0)
                                            throw new Exception("No puedes cambiar la modalidad porque ya tienes pagos completados.");

                                        if (dbTramite > 0)
                                            throw new Exception("No puedes cambiar la modalidad porque tienes un pago en revisión o pendiente de comprobante.");

                                        if (dbPendientesRecientes > 0)
                                            throw new Exception("Tienes un intento de pago en proceso. Por seguridad, espera 30 minutos desde tu último intento para poder cambiar de modalidad.");

                                        // Si llega aquí y hay cambio de plan, significa que solo hay transacciones 'pending' abandonadas (más de 30 mins)
                                        // 1. Expiramos primero la transacción en la tabla D para evitar que un Webhook tardío de Stripe intente procesarla
                                        string sqlExpireD = @"
                                        UPDATE ""Eventos_D_Transacciones"" 
                                        SET ""Estatus_Pago"" = 'expired', ""Completado"" = TRUE
                                        WHERE ""Estatus_Pago"" = 'pending' 
                                        AND ""Id_Transaccion"" IN (
                                            SELECT ""Id_Transaccion"" FROM ""Eventos_E_Pagos_Aplicados"" 
                                            WHERE ""Id_Cuenta"" IN (SELECT ""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA)
                                        )";
                                        using (var cmdExpD = new NpgsqlCommand(sqlExpireD, conexion, trans))
                                        {
                                            cmdExpD.Parameters.AddWithValue("@idA", idAsis);
                                            await cmdExpD.ExecuteNonQueryAsync();
                                        }

                                        // 2. Ahora sí procedemos a limpiar los puentes de forma segura
                                        string sqlDelE = @"DELETE FROM ""Eventos_E_Pagos_Aplicados"" 
                                        WHERE ""Id_Cuenta"" IN (SELECT ""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA)";
                                        using (var cmdDelE = new NpgsqlCommand(sqlDelE, conexion, trans))
                                        {
                                            cmdDelE.Parameters.AddWithValue("@idA", idAsis);
                                            await cmdDelE.ExecuteNonQueryAsync();
                                        }
                                    }

                                    // 2. Actualizar Datos Principales (Nombre, Edad, Género, Subtipo)
                                    string sqlUp = @"UPDATE ""Eventos_B_Asistentes"" SET ""Nombre_Completo""=@nom, ""Etiqueta_Grupo""=@grupo, ""Edad""=@ed, ""Genero""=@gen, ""Id_Subtipo""=@sub WHERE ""Id_Asistente""=@idA";
                                    using (var cmd = new NpgsqlCommand(sqlUp, conexion, trans))
                                    {
                                        SetParamsAsistente(cmd, idRegistro, asis, idSubtipoDB);
                                        cmd.Parameters.AddWithValue("@idA", idAsis);
                                        await cmd.ExecuteNonQueryAsync();
                                    }

                                    // 3. Regenerar Deuda ¡SOLO SI CAMBIÓ DE PLAN O NO TIENE DEUDAS PREVIAS (Viene de Registro Rápido)!
                                    if (cambioDePlan || dbTotalCuentas == 0)
                                    {
                                        // IMPORTANTE: Protegemos los productos extra borrando únicamente las cuentas de modalidad (Numero_Pago > 0 o Numero_Pago = -1 o NULL)
                                        string sqlDelDeudaMod = @"DELETE FROM ""Eventos_C_Cuentas_Cobrar"" 
                                                                  WHERE ""Id_Asistente"" = @idA 
                                                                    AND (""Id_Calendario"" IS NULL OR ""Id_Calendario"" IN (SELECT ""Id_Calendario"" FROM ""Eventos_Calendario"" WHERE ""Numero_Pago"" > 0 OR ""Numero_Pago"" = -1))";
                                        using (var cmdDelMod = new NpgsqlCommand(sqlDelDeudaMod, conexion, trans))
                                        {
                                            cmdDelMod.Parameters.AddWithValue("@idA", idAsis);
                                            await cmdDelMod.ExecuteNonQueryAsync();
                                        }

                                        string sqlDeuda = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"")
                                                            SELECT @idA, ""Id_Calendario"", 
                                                            CASE WHEN (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) > 0 THEN
                                                                ROUND(
                                                                    @costo * ( CAST(""Monto_Base"" AS DECIMAL) / (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) )
                                                                , 2)
                                                            ELSE @costo END, 
                                                            FALSE
                                                            FROM ""Eventos_Calendario"" 
                                                            WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))
                                                            ORDER BY ""Numero_Pago"" ASC";

                                        using (var cmd = new NpgsqlCommand(sqlDeuda, conexion, trans))
                                        {
                                            cmd.Parameters.AddWithValue("@idA", idAsis);
                                            cmd.Parameters.AddWithValue("@ev", modelo.Id_Evento);
                                            cmd.Parameters.AddWithValue("@costo", costoFinal);
                                            var paramSub = new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer);
                                            paramSub.Value = idSubtipoDB ?? DBNull.Value;
                                            cmd.Parameters.Add(paramSub);
                                            int gen = await cmd.ExecuteNonQueryAsync();
                                            if (gen == 0) await new NpgsqlCommand($"INSERT INTO \"Eventos_C_Cuentas_Cobrar\" (\"Id_Asistente\", \"Monto_Pagar\", \"Id_Calendario\", \"Pagado\") VALUES ({idAsis}, {costoFinal}, NULL, FALSE)", conexion, trans).ExecuteNonQueryAsync();
                                        }
                                    }

                                    // 4. SINCRONIZACIÓN DE PRODUCTOS EXTRA EN EDICIÓN (SOLO NO PAGADOS)
                                    if (asis.ProductosExtra != null && asis.ProductosExtra.Count > 0)
                                    {
                                        foreach (var pe in asis.ProductosExtra)
                                        {
                                            decimal precioExtra = 0;
                                            int cantMax = 999;
                                            using (var cmdCat = new NpgsqlCommand(@"SELECT ""Precio"", ""Cantidad_Maxima"" FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_ProductoExtra"" = @idP", conexion, trans))
                                            {
                                                cmdCat.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                using (var rCat = await cmdCat.ExecuteReaderAsync())
                                                {
                                                    if (await rCat.ReadAsync())
                                                    {
                                                        precioExtra = (decimal)rCat["Precio"];
                                                        cantMax = (int)rCat["Cantidad_Maxima"];
                                                    }
                                                }
                                            }

                                            int idCalExtra = 0;
                                            using (var cmdFind = new NpgsqlCommand(@"SELECT ""Id_Calendario"" FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @idEv AND ""Numero_Pago"" = -(10000 + @idP) LIMIT 1", conexion, trans))
                                            {
                                                cmdFind.Parameters.AddWithValue("@idEv", modelo.Id_Evento);
                                                cmdFind.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                var existing = await cmdFind.ExecuteScalarAsync();
                                                if (existing != null && existing != DBNull.Value) idCalExtra = Convert.ToInt32(existing);
                                            }

                                            if (idCalExtra == 0 && pe.Cantidad > 0)
                                            {
                                                string sqlFix = @"INSERT INTO ""Eventos_Calendario"" (""Id_Evento"", ""Numero_Pago"", ""Nombre_Concepto"", ""Monto_Base"", ""Fecha_Limite"") 
                                                                  VALUES (
                                                                      @idEv, 
                                                                      -(10000 + @idP), 
                                                                      COALESCE((SELECT ""Nombre_Producto"" FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_ProductoExtra""=@idP), 'Producto Extra'), 
                                                                      @precio, 
                                                                      COALESCE((SELECT MAX(""Fecha_Limite"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @idEv AND ""Numero_Pago"" > 0), NOW() + INTERVAL '30 days')
                                                                  ) 
                                                                  RETURNING ""Id_Calendario""";
                                                using (var cmdFix = new NpgsqlCommand(sqlFix, conexion, trans))
                                                {
                                                    cmdFix.Parameters.AddWithValue("@idEv", modelo.Id_Evento);
                                                    cmdFix.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                    cmdFix.Parameters.AddWithValue("@precio", precioExtra);
                                                    idCalExtra = Convert.ToInt32(await cmdFix.ExecuteScalarAsync());
                                                }
                                            }

                                            int qPagada = 0;
                                            int qPendiente = 0;
                                            if (idCalExtra > 0)
                                            {
                                                string sqlCuentas = @"SELECT 
                                                    COUNT(CASE WHEN ""Pagado"" = TRUE THEN 1 END) as ""CantPagada"",
                                                    COUNT(CASE WHEN ""Pagado"" = FALSE THEN 1 END) as ""CantPendiente""
                                                    FROM ""Eventos_C_Cuentas_Cobrar""
                                                    WHERE ""Id_Asistente"" = @idA AND ""Id_Calendario"" = @idCal";
                                                using (var cmdCuentas = new NpgsqlCommand(sqlCuentas, conexion, trans))
                                                {
                                                    cmdCuentas.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdCuentas.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    using (var rC = await cmdCuentas.ExecuteReaderAsync())
                                                    {
                                                        if (await rC.ReadAsync())
                                                        {
                                                            qPagada = Convert.ToInt32(rC["CantPagada"]);
                                                            qPendiente = Convert.ToInt32(rC["CantPendiente"]);
                                                        }
                                                    }
                                                }
                                            }
                                            int qActual = qPagada + qPendiente;
                                            int qSolicitada = pe.Cantidad;

                                            // CANDADO DE SEGURIDAD
                                            if (qSolicitada < qPagada)
                                            {
                                                throw new Exception($"No es posible reducir este adicional por debajo de {qPagada} unidad(es) porque ya han sido pagadas.");
                                            }
                                            if (qSolicitada > cantMax)
                                            {
                                                throw new Exception($"La cantidad solicitada ({qSolicitada}) supera el máximo permitido ({cantMax}).");
                                            }

                                            if (qSolicitada > qActual)
                                            {
                                                // AUMENTAR
                                                int delta = qSolicitada - qActual;
                                                for (int i = 0; i < delta; i++)
                                                {
                                                    string sqlAdd = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"") 
                                                                      VALUES (@idA, @idCal, @precio, FALSE)";
                                                    using (var cmdAdd = new NpgsqlCommand(sqlAdd, conexion, trans))
                                                    {
                                                        cmdAdd.Parameters.AddWithValue("@idA", idAsis);
                                                        cmdAdd.Parameters.AddWithValue("@idCal", idCalExtra);
                                                        cmdAdd.Parameters.AddWithValue("@precio", precioExtra);
                                                        await cmdAdd.ExecuteNonQueryAsync();
                                                    }
                                                }

                                                bool existePE = false;
                                                using (var cmdCheck = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Eventos_Asistentes_ProductosExtra"" WHERE ""Id_Asistente"" = @idA AND ""Id_ProductoExtra"" = @idP", conexion, trans))
                                                {
                                                    cmdCheck.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdCheck.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                    existePE = (long)await cmdCheck.ExecuteScalarAsync() > 0;
                                                }

                                                if (existePE)
                                                {
                                                    using (var cmdUpdPE = new NpgsqlCommand(@"UPDATE ""Eventos_Asistentes_ProductosExtra"" SET ""Cantidad"" = @cant, ""Total"" = (@precio * @cant) WHERE ""Id_Asistente"" = @idA AND ""Id_ProductoExtra"" = @idP", conexion, trans))
                                                    {
                                                        cmdUpdPE.Parameters.AddWithValue("@idA", idAsis);
                                                        cmdUpdPE.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                        cmdUpdPE.Parameters.AddWithValue("@cant", qSolicitada);
                                                        cmdUpdPE.Parameters.AddWithValue("@precio", precioExtra);
                                                        await cmdUpdPE.ExecuteNonQueryAsync();
                                                    }
                                                }
                                                else
                                                {
                                                    using (var cmdInsPE = new NpgsqlCommand(@"INSERT INTO ""Eventos_Asistentes_ProductosExtra"" (""Id_Asistente"", ""Id_ProductoExtra"", ""Cantidad"", ""Precio_Unitario"", ""Total"") VALUES (@idA, @idP, @cant, @precio, (@precio * @cant))", conexion, trans))
                                                    {
                                                        cmdInsPE.Parameters.AddWithValue("@idA", idAsis);
                                                        cmdInsPE.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                        cmdInsPE.Parameters.AddWithValue("@cant", qSolicitada);
                                                        cmdInsPE.Parameters.AddWithValue("@precio", precioExtra);
                                                        await cmdInsPE.ExecuteNonQueryAsync();
                                                    }
                                                }
                                            }
                                            else if (qSolicitada < qActual)
                                            {
                                                // DISMINUIR (solo de las NO pagadas)
                                                // 1. Validar que no haya pagos en proceso para este adicional
                                                string sqlCheckTrxExtra = @"
                                                SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d
                                                JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion"" = e.""Id_Transaccion""
                                                JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                                                WHERE c.""Id_Asistente"" = @idA AND c.""Id_Calendario"" = @idCal AND c.""Pagado"" = FALSE
                                                  AND (d.""Estatus_Pago"" IN ('waiting_proof', 'review') OR (d.""Estatus_Pago"" = 'pending' AND d.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes'))";
                                                using (var cmdChkTrx = new NpgsqlCommand(sqlCheckTrxExtra, conexion, trans))
                                                {
                                                    cmdChkTrx.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdChkTrx.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    long enProceso = (long)await cmdChkTrx.ExecuteScalarAsync();
                                                    if (enProceso > 0)
                                                        throw new Exception("Tienes un intento de pago en proceso o revisión para este adicional. Espera 30 minutos o dictamínalo antes de reducir la cantidad.");
                                                }

                                                // 2. Expirar transacciones abandonadas (> 30 mins) y limpiar puentes en E para evitar error de Foreign Key
                                                string sqlExpExtraD = @"
                                                UPDATE ""Eventos_D_Transacciones""
                                                SET ""Estatus_Pago"" = 'expired', ""Completado"" = TRUE
                                                WHERE ""Estatus_Pago"" = 'pending'
                                                  AND ""Id_Transaccion"" IN (
                                                      SELECT e.""Id_Transaccion"" FROM ""Eventos_E_Pagos_Aplicados"" e
                                                      JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                                                      WHERE c.""Id_Asistente"" = @idA AND c.""Id_Calendario"" = @idCal AND c.""Pagado"" = FALSE
                                                  )";
                                                using (var cmdExpD = new NpgsqlCommand(sqlExpExtraD, conexion, trans))
                                                {
                                                    cmdExpD.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdExpD.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    await cmdExpD.ExecuteNonQueryAsync();
                                                }

                                                string sqlDelExtraE = @"
                                                DELETE FROM ""Eventos_E_Pagos_Aplicados""
                                                WHERE ""Id_Cuenta"" IN (
                                                    SELECT c.""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" c
                                                    WHERE c.""Id_Asistente"" = @idA AND c.""Id_Calendario"" = @idCal AND c.""Pagado"" = FALSE
                                                )";
                                                using (var cmdDelE = new NpgsqlCommand(sqlDelExtraE, conexion, trans))
                                                {
                                                    cmdDelE.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdDelE.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    await cmdDelE.ExecuteNonQueryAsync();
                                                }

                                                int delta = qActual - qSolicitada;
                                                string sqlDelCuentas = @"
                                                DELETE FROM ""Eventos_C_Cuentas_Cobrar""
                                                WHERE ""Id_Cuenta"" IN (
                                                    SELECT ""Id_Cuenta"" 
                                                    FROM ""Eventos_C_Cuentas_Cobrar"" 
                                                    WHERE ""Id_Asistente"" = @idA 
                                                      AND ""Id_Calendario"" = @idCal 
                                                      AND ""Pagado"" = FALSE
                                                    ORDER BY ""Id_Cuenta"" DESC
                                                    LIMIT @delta
                                                )";
                                                using (var cmdDelC = new NpgsqlCommand(sqlDelCuentas, conexion, trans))
                                                {
                                                    cmdDelC.Parameters.AddWithValue("@idA", idAsis);
                                                    cmdDelC.Parameters.AddWithValue("@idCal", idCalExtra);
                                                    cmdDelC.Parameters.AddWithValue("@delta", delta);
                                                    await cmdDelC.ExecuteNonQueryAsync();
                                                }

                                                if (qSolicitada == 0)
                                                {
                                                    using (var cmdDelPE = new NpgsqlCommand(@"DELETE FROM ""Eventos_Asistentes_ProductosExtra"" WHERE ""Id_Asistente"" = @idA AND ""Id_ProductoExtra"" = @idP", conexion, trans))
                                                    {
                                                        cmdDelPE.Parameters.AddWithValue("@idA", idAsis);
                                                        cmdDelPE.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                        await cmdDelPE.ExecuteNonQueryAsync();
                                                    }
                                                }
                                                else
                                                {
                                                    using (var cmdUpdPE = new NpgsqlCommand(@"UPDATE ""Eventos_Asistentes_ProductosExtra"" SET ""Cantidad"" = @cant, ""Total"" = (@precio * @cant) WHERE ""Id_Asistente"" = @idA AND ""Id_ProductoExtra"" = @idP", conexion, trans))
                                                    {
                                                        cmdUpdPE.Parameters.AddWithValue("@idA", idAsis);
                                                        cmdUpdPE.Parameters.AddWithValue("@idP", pe.Id_ProductoExtra);
                                                        cmdUpdPE.Parameters.AddWithValue("@cant", qSolicitada);
                                                        cmdUpdPE.Parameters.AddWithValue("@precio", precioExtra);
                                                        await cmdUpdPE.ExecuteNonQueryAsync();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                decimal sumaDeudaModalidad = 0;
                                string sqlSumMod = @"SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) 
                                                     FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                                     JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                                     WHERE c.""Id_Asistente"" = @idA AND cal.""Numero_Pago"" > 0";
                                using (var cmdSum = new NpgsqlCommand(sqlSumMod, conexion, trans))
                                {
                                    cmdSum.Parameters.AddWithValue("@idA", idAsis);
                                    sumaDeudaModalidad = (decimal)await cmdSum.ExecuteScalarAsync();
                                }
                                decimal difModalidad = costoFinal - sumaDeudaModalidad;
                                if (difModalidad != 0 && Math.Abs(difModalidad) < 5 && sumaDeudaModalidad > 0)
                                {
                                    string sqlAjuste = @"UPDATE ""Eventos_C_Cuentas_Cobrar"" 
                                                         SET ""Monto_Pagar"" = ""Monto_Pagar"" + @dif 
                                                         WHERE ""Id_Cuenta"" = (
                                                             SELECT c.""Id_Cuenta"" 
                                                             FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                                             JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                                             WHERE c.""Id_Asistente"" = @idA AND c.""Pagado"" = FALSE AND cal.""Numero_Pago"" > 0 
                                                             ORDER BY c.""Id_Cuenta"" DESC LIMIT 1
                                                         )";
                                    using (var cmdAdj = new NpgsqlCommand(sqlAjuste, conexion, trans)) { cmdAdj.Parameters.AddWithValue("@dif", difModalidad); cmdAdj.Parameters.AddWithValue("@idA", idAsis); await cmdAdj.ExecuteNonQueryAsync(); }
                                }

                                if (asis.Respuestas != null)
                                {
                                    await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\"={idAsis}", conexion, trans).ExecuteNonQueryAsync();
                                    string sqlR = @"INSERT INTO ""Eventos_Respuestas"" (""Id_Asistente"", ""Id_Pregunta"", ""Valor_Respuesta"") VALUES (@idA, @idP, @val)";
                                    foreach (var r in asis.Respuestas)
                                    {
                                        if (!string.IsNullOrEmpty(r.Valor))
                                        {
                                            using (var cmd = new NpgsqlCommand(sqlR, conexion, trans))
                                            {
                                                cmd.Parameters.AddWithValue("@idA", idAsis); cmd.Parameters.AddWithValue("@idP", r.Id_Pregunta); cmd.Parameters.AddWithValue("@val", r.Valor); await cmd.ExecuteNonQueryAsync();
                                            }
                                        }
                                    }
                                }

                                string accionTxt = (idAsis == asis.Id_Asistente) ? "Actualizó" : "Registró";
                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                                    Parametros.AccionesBitacora.Crear,
                                    $"{accionTxt} asistente {asis.NombreCompleto} (ID: {idAsis}) en Evento #{modelo.Id_Evento}",
                                    HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);
                            }
                            await trans.CommitAsync();
                            MostrarMensaje("¡Listo!", "Información guardada correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception ex) { await trans.RollbackAsync(); throw ex; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Registro", new { sid = _sid });
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ActualizarGrupoMasivo(int idEvento, List<int> idsAsistentes, string nuevoGrupo)
        {
            try
            {
                using var con = new NpgsqlConnection(_cadenaConexion);
                await con.OpenAsync();

                if (await ValidarEventoBloqueado(idEvento, con))
                    return Json(new { exito = false, mensaje = "El evento está bloqueado." });

                string sql = @"UPDATE ""Eventos_B_Asistentes"" SET ""Etiqueta_Grupo"" = @grupo 
                       WHERE ""Id_Asistente"" = ANY(@ids)";
                using var cmd = new NpgsqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@grupo", string.IsNullOrWhiteSpace(nuevoGrupo) ? "General" : nuevoGrupo);
                cmd.Parameters.AddWithValue("@ids", idsAsistentes);
                await cmd.ExecuteNonQueryAsync();
                return Json(new { exito = true });
            }
            catch { return Json(new { exito = false }); }
        }

        /// <summary>
        /// Sirve para asignar los parámetros comunes de un asistente en un comando Npgsql.
        /// Se hizo así para evitar repetir código en inserción y actualización.
        /// </summary>
        /// <param name="cmd">Es el comando NpgsqlCommand donde se agregarán los parámetros.</param>
        /// <param name="idReg">Es el ID del registro padre.</param>
        /// <param name="asis">Es el objeto AsistenteItem con los datos del asistente.</param>
        /// <param name="idSub">Es el ID del subtipo seleccionado o DBNull.Value.</param>
        private void SetParamsAsistente(NpgsqlCommand cmd, int idReg, AsistenteItem asis, object idSub)
        {
            cmd.Parameters.AddWithValue("@reg", idReg);
            cmd.Parameters.AddWithValue("@nom", asis.NombreCompleto);
            cmd.Parameters.AddWithValue("@grupo", string.IsNullOrWhiteSpace(asis.EtiquetaGrupo) ? "General" : asis.EtiquetaGrupo);
            cmd.Parameters.AddWithValue("@ed", asis.Edad);
            cmd.Parameters.AddWithValue("@gen", asis.Genero);
            cmd.Parameters.AddWithValue("@sub", idSub);
            cmd.Parameters.AddWithValue("@token", asis.TokenExterno ?? (object)DBNull.Value);
        }

        /// <summary>
        /// Función para preparar el pago de los asistentes seleccionados.
        /// </summary>
        /// <param name="idEvento">Es el ID del evento.</param>
        /// <param name="idsSeleccionados">Es la lista de IDs de asistentes seleccionados para pagar.</param>
        /// <returns>Es la redirección a la acción de pago.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PrepararPago(int idEvento, List<int> idsSeleccionados)
        {
            string _sid = Funciones.EncriptarId(idEvento);

            if (idsSeleccionados == null || !idsSeleccionados.Any())
            {
                MostrarMensaje("Atención", "Debes seleccionar al menos una persona para continuar.", TipoMensaje.Alerta);
                return RedirectToAction("Registro", new { sid = _sid });
            }

            // --- BLOQUEO DE SEGURIDAD DE UX (Antes de entrar al carrito) ---
            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();

                // Excluimos a los seleccionados para saber si, quitándolos a ellos, aún queda espacio libre en el evento
                var disp = await ConsultarDisponibilidadEvento(idEvento, conexion, null, false, idsSeleccionados);

                if (disp != null)
                {
                    if (disp.Modalidades != null && disp.Modalidades.Any())
                    {
                        string sqlSubAsis = @"SELECT ""Id_Subtipo"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                        var subtiposPagar = new List<int>();
                        using (var cmdSubAsis = new NpgsqlCommand(sqlSubAsis, conexion))
                        {
                            cmdSubAsis.Parameters.AddWithValue("@ids", idsSeleccionados);
                            using (var rSub = await cmdSubAsis.ExecuteReaderAsync())
                            {
                                while (await rSub.ReadAsync())
                                {
                                    if (rSub[0] != DBNull.Value) subtiposPagar.Add(Convert.ToInt32(rSub[0]));
                                }
                            }
                        }

                        var conteoPorModalidad = subtiposPagar.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

                        foreach (var kvp in conteoPorModalidad)
                        {
                            var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == kvp.Key);
                            if (modal != null && modal.LugaresDisponibles < kvp.Value)
                            {
                                MostrarMensaje("Cupo Agotado", $"Lo sentimos, la modalidad '{modal.NombreModalidad}' acaba de agotarse o redujo su límite. Ya no hay lugares suficientes para el grupo seleccionado.", TipoMensaje.Error);
                                return RedirectToAction("Registro", new { sid = _sid });
                            }
                        }
                    }
                    else
                    {
                        if (disp.CupoMaximoEvento > 0 && disp.DisponiblesGlobal < idsSeleccionados.Count)
                        {
                            MostrarMensaje("Cupo Agotado", "Lo sentimos, el evento acaba de agotarse o redujo su límite. Ya no hay lugares suficientes para el grupo seleccionado.", TipoMensaje.Error);
                            return RedirectToAction("Registro", new { sid = _sid });
                        }
                    }
                }
            }

            TempData["IdsSeguros"] = System.Text.Json.JsonSerializer.Serialize(idsSeleccionados);
            return RedirectToAction("Pagar");
        }

        /// <summary>
        /// Es el método que muestra la vista de pago para los asistentes seleccionados, ya sea en modo invitado (con token) o interno (usuario logueado).
        /// </summary>
        /// <param name="token">Es el token de invitado para acceso externo, si es que se accede por esa vía.</param>
        /// <param name="idsSeleccionados">Es la lista de IDs de asistentes seleccionados para pagar, que puede venir por parámetro o por TempData.</param>
        /// <param name="session_id">Es el parámetro que Stripe envía al retornar del proceso de pago, para validar el resultado.</param>
        /// <param name="pagosSeleccionados">Es la lista de IDs de pagos seleccionados para mostrar en la vista, que puede venir por parámetro (ej. reintento) o ser manejada internamente.</param>
        /// <param name="mostrarFuturos">Es un flag opcional para indicar si se deben mostrar también los pagos futuros en la vista, que puede venir por parámetro o ser manejado internamente.</param>
        /// <returns>Retorna la vista de pago con la información correspondiente.</returns>
        [AllowAnonymous]
        [HttpGet("Eventos/Pagar/{token?}")]
        public async Task<IActionResult> Pagar(string token, List<int> idsSeleccionados, string session_id, List<string> pagosSeleccionados = null, bool mostrarFuturos = false)
        {
            // -----------------------------------------------------------------------
            // A. VALIDACIÓN RETORNO STRIPE (Lógica exacta de Versión A)
            // -----------------------------------------------------------------------
            if (!string.IsNullOrEmpty(session_id))
            {
                try
                {
                    var service = new Stripe.Checkout.SessionService();
                    var session = await service.GetAsync(session_id);

                    if (session.PaymentStatus == "paid")
                    {
                        // Extraer ID de la referencia de Stripe (Formato esperado: EVT_{ID}_{GUID})
                        string[] partesRef = session.ClientReferenceId?.Split('_');

                        if (partesRef != null && partesRef.Length >= 2 && int.TryParse(partesRef[1], out int idTrx))
                        {
                            using (var conexion = new NpgsqlConnection(_cadenaConexion))
                            {
                                await conexion.OpenAsync();

                                // 1. Recuperar referencia guardada en BD
                                string sqlVal = @"SELECT ""External_Reference"" FROM ""Eventos_D_Transacciones"" WHERE ""Id_Transaccion"" = @id";
                                string refGuardada = "";

                                using (var cmdVal = new NpgsqlCommand(sqlVal, conexion))
                                {
                                    cmdVal.Parameters.AddWithValue("@id", idTrx);
                                    var res = await cmdVal.ExecuteScalarAsync();
                                    if (res != null) refGuardada = res.ToString();
                                }

                                // 2. COMPARACIÓN EXACTA DE SEGURIDAD
                                if (refGuardada == session.ClientReferenceId)
                                {
                                    // Aplicar pago (Lógica de negocio externa)
                                    await AplicarPagoExitoso(idTrx, session.PaymentIntentId ?? session.Id);
                                    ViewBag.EstadoPago = "approved";
                                }
                                else
                                {
                                    ViewBag.EstadoPago = "error"; // Referencia no coincide (posible ataque)
                                }
                            }
                        }
                        else
                        {
                            ViewBag.EstadoPago = "error"; // Formato inválido
                        }
                    }
                    else
                    {
                        ViewBag.EstadoPago = "pending";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error validando retorno: " + ex.Message);
                    ViewBag.EstadoPago = "error";
                }
            }

            // -----------------------------------------------------------------------
            // B. SEGURIDAD Y RECUPERACIÓN DE DATOS (Usuario vs Invitado)
            // -----------------------------------------------------------------------
            var idsAsistentesAConsultar = new List<int>();

            if (TempData["IdsSeguros"] != null)
            {
                try
                {
                    string jsonIds = TempData["IdsSeguros"].ToString();
                    idsAsistentesAConsultar = System.Text.Json.JsonSerializer.Deserialize<List<int>>(jsonIds);
                    TempData.Keep("IdsSeguros");
                }
                catch { }
            }

            // Si viene por parámetro (ej. reintento), usarlos con precaución (se validarán abajo)
            if (idsSeleccionados != null && idsSeleccionados.Any()) idsAsistentesAConsultar = idsSeleccionados;

            bool esInvitado = !string.IsNullOrEmpty(token);
            int idUsuarioActual = 0;
            int idEvento = 0;
            string idEventoEncriptado = "";

            if (esInvitado)
            {
                ViewBag.Modo = "Invitado";
                ViewBag.Token = token;
                try
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();
                        // Validar Token y obtener IDs
                        string sqlTok = @"SELECT b.""Id_Asistente"", r.""Id_Evento"", r.""Id_Usuario"" 
                                  FROM ""Eventos_B_Asistentes"" b 
                                  JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                  WHERE b.""Token_Pago_Externo"" = @tok";
                        using (var cmd = new NpgsqlCommand(sqlTok, conexion))
                        {
                            cmd.Parameters.AddWithValue("@tok", token);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    // Sobrescribimos IDs por seguridad: un invitado solo ve SU asistente
                                    idsAsistentesAConsultar.Clear();
                                    idsAsistentesAConsultar.Add((int)r["Id_Asistente"]);
                                    idEvento = (int)r["Id_Evento"];
                                    idEventoEncriptado = Funciones.EncriptarId(idEvento);
                                }
                                else return RedirectToAction("Index", "Home");
                            }
                        }
                    }
                }
                catch { }

                // Validar pagos pendientes automáticos
                if (idsAsistentesAConsultar.Any())
                    await VerificarPagosPorAsistente(idsAsistentesAConsultar.First());
            }
            else
            {
                // Modo Interno
                if (!User.Identity.IsAuthenticated) return Challenge();
                idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

                if (!idsAsistentesAConsultar.Any())
                {
                    MostrarMensaje("Error", "No has seleccionado ningún asistente", TipoMensaje.Alerta);
                    return RedirectToAction("Index", "Eventos"); 
                }
                ViewBag.Modo = "Interno";

                try
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();
                        // Validar que los IDs pertenezcan al usuario logueado
                        string sqlVal = @"SELECT b.""Id_Asistente"", r.""Id_Evento""
                                  FROM ""Eventos_B_Asistentes"" b 
                                  JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                  WHERE r.""Id_Usuario"" = @uid AND b.""Id_Asistente"" = ANY(@ids)";

                        var idsValidos = new List<int>();
                        using (var cmd = new NpgsqlCommand(sqlVal, conexion))
                        {
                            cmd.Parameters.AddWithValue("@uid", idUsuarioActual);
                            cmd.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    idsValidos.Add((int)r["Id_Asistente"]);
                                    idEvento = (int)r["Id_Evento"];
                                    idEventoEncriptado = Funciones.EncriptarId(idEvento);
                                }
                            }
                        }
                        idsAsistentesAConsultar = idsValidos;
                    }
                }
                catch { }

                await VerificarPagosPendientes();
            }

            // -----------------------------------------------------------------------
            // C. BLOQUEO DE SEGURIDAD (Transacciones en proceso)
            // -----------------------------------------------------------------------
            if (idsAsistentesAConsultar.Any())
            {
                try
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();
                        string sqlCheckBloqueo = @"
                    SELECT d.""Estatus_Pago"", d.""Id_Transaccion""
                    FROM ""Eventos_D_Transacciones"" d
                    JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion"" = e.""Id_Transaccion""
                    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                    WHERE c.""Id_Asistente"" = ANY(@ids)
                      AND d.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected')
                    LIMIT 1";

                        using (var cmd = new NpgsqlCommand(sqlCheckBloqueo, conexion))
                        {
                            cmd.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    string estatus = r["Estatus_Pago"].ToString();
                                    int idTrx = (int)r["Id_Transaccion"];

                                    if (estatus == "rejected" || estatus == "waiting_proof")
                                        return RedirectToAction("RetomarTransferencia", new { idTransaccion = idTrx, token = token });
                                    else if (estatus == "review")
                                    {
                                        MostrarMensaje("Pago en Proceso", "Tu pago anterior está siendo validado.", TipoMensaje.Info);
                                        return RedirectToAction("MisTransferencias", new { id = idEvento, token = token });
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // -----------------------------------------------------------------------
            // D. PREPARACIÓN DE VISTA Y CÁLCULOS 
            // -----------------------------------------------------------------------
            // Si no viene la bandera 'filtroAplicado', es la primera vez que entra.
            // Forzamos mostrarFuturos a TRUE por defecto.
            bool esPrimeraVisita = !Request.Query.ContainsKey("filtroAplicado");
            if (esPrimeraVisita)
            {
                mostrarFuturos = true;
            }

            var modelo = new ConfirmarPagoViewModel
            {
                MostrarPagosFuturos = mostrarFuturos,
                IdsPagosSeleccionados = pagosSeleccionados ?? new List<string>()
            };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // D.1 Datos del Evento
                    int idEncRequisitoPagar = 0;
                    string claveEncuestaPagar = "";
                    string sqlEv = @"SELECT ""Titulo"", ""Fecha_Inicio"", ""Permitir_Pago"", ""Permitir_Transferencia"", ""Permitir_Pago_Tarjeta"", ""Cobrar_Comision_Extra"", ""Imagen_Url"",
                                            ""Id_Encuesta_Requisito"",
                                            (SELECT enc.""Clave_Url"" FROM ""Encuestas_Catalogo"" enc WHERE enc.""Id_Encuesta"" = ""Eventos_Catalogo"".""Id_Encuesta_Requisito"") as ""ClaveEncuesta""
                                     FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @ev";
                    using (var cmd = new NpgsqlCommand(sqlEv, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.IdEvento = idEvento;
                                modelo.TituloEvento = r["Titulo"].ToString();
                                modelo.FechaEvento = (DateTime)r["Fecha_Inicio"];
                                ViewBag.EsTransferencia = r["Permitir_Transferencia"] != DBNull.Value && (bool)r["Permitir_Transferencia"];
                                ViewBag.PermitirTarjeta = r["Permitir_Pago_Tarjeta"] != DBNull.Value ? (bool)r["Permitir_Pago_Tarjeta"] : true;
                                modelo.CobrarComisionExtra = r["Cobrar_Comision_Extra"] != DBNull.Value ? (bool)r["Cobrar_Comision_Extra"] : true;

                                if (r["Id_Encuesta_Requisito"] != DBNull.Value) idEncRequisitoPagar = Convert.ToInt32(r["Id_Encuesta_Requisito"]);
                                claveEncuestaPagar = r["ClaveEncuesta"]?.ToString();
                                ViewBag.TieneEncuestaRequisito = idEncRequisitoPagar > 0;
                                ViewBag.ClaveEncuesta = claveEncuestaPagar;
                                ViewBag.UrlEncuesta = claveEncuestaPagar;
                                ViewBag.IdEncuestaRequisito = idEncRequisitoPagar;

                                // Pasamos la imagen a la vista para el efecto de la tarjeta difuminada
                                ViewBag.ImagenEvento = r["Imagen_Url"]?.ToString();

                                if (!(bool)r["Permitir_Pago"])
                                {
                                    MostrarMensaje("Aviso", "Pagos cerrados.", TipoMensaje.Alerta);
                                    return esInvitado ? RedirectToAction("Index", "Home") : RedirectToAction("Registro", new { sid = idEventoEncriptado });
                                }
                            }
                        }
                    }

                    // D.2 Carga de Personas y Deudas
                    var personas = new List<AgrupacionDeudaUsuario>();
                    // Modificar la consulta SQL para traer Token_Pago_Externo
                    string sqlNombres = @"SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Token_Pago_Externo"",
                                                 COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                                                           WHERE h.""Id_Encuesta"" = @idEnc 
                                                             AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida""
                                          FROM ""Eventos_B_Asistentes"" b 
                                          WHERE b.""Id_Asistente"" = ANY(@ids)";

                    using (var cmd = new NpgsqlCommand(sqlNombres, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                        cmd.Parameters.AddWithValue("@idEnc", idEncRequisitoPagar);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                personas.Add(new AgrupacionDeudaUsuario
                                {
                                    IdRegistro = (int)r["Id_Asistente"],
                                    IdAsistente = (int)r["Id_Asistente"],
                                    NombreAsistente = r["Nombre_Completo"].ToString(),
                                    TokenExterno = r["Token_Pago_Externo"].ToString(),
                                    EncuestaRespondida = idEncRequisitoPagar > 0 ? (bool)r["EncuestaRespondida"] : true
                                });
                            }
                        }
                    }

                    decimal subTotalCalculado = 0;

                    foreach (var p in personas)
                    {
                        string sqlDeuda = @"SELECT c.""Id_Cuenta"", c.""Monto_Pagar"", c.""Pagado"", 
                                    CASE 
                                        WHEN cal.""Numero_Pago"" <= -10000 AND pe.""Cantidad"" > 1 THEN 
                                            cal.""Nombre_Concepto"" || ' (Unidad ' || ROW_NUMBER() OVER(PARTITION BY c.""Id_Asistente"", c.""Id_Calendario"" ORDER BY c.""Id_Cuenta"") || ' de ' || pe.""Cantidad"" || ')'
                                        ELSE cal.""Nombre_Concepto"" 
                                    END AS ""Nombre_Concepto"", 
                                    cal.""Numero_Pago"", cal.""Fecha_Limite"",
                                    cat.""Descripcion"" AS ""Descripcion_Extra""
                                    FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                    LEFT JOIN ""Eventos_Asistentes_ProductosExtra"" pe 
                                           ON pe.""Id_Asistente"" = c.""Id_Asistente"" 
                                          AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"")
                                    LEFT JOIN ""Eventos_Catalogo_ProductosExtra"" cat
                                           ON cat.""Id_ProductoExtra"" = -(cal.""Numero_Pago"" + 10000)
                                    WHERE c.""Id_Asistente"" = @asis 
                                    ORDER BY CASE WHEN cal.""Numero_Pago"" > 0 THEN cal.""Numero_Pago"" ELSE 99999 + ABS(cal.""Numero_Pago"") END ASC";

                        using (var cmd = new NpgsqlCommand(sqlDeuda, conexion))
                        {
                            cmd.Parameters.AddWithValue("@asis", p.IdRegistro);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    var item = new ItemDeudaPago
                                    {
                                        IdCalendario = (int)r["Id_Cuenta"],
                                        NumeroPago = (int)r["Numero_Pago"],
                                        Monto = (decimal)r["Monto_Pagar"],
                                        FechaLimite = (DateTime)r["Fecha_Limite"],
                                        Concepto = r["Nombre_Concepto"]?.ToString() ?? $"Pago {r["Numero_Pago"]}",
                                        Descripcion = r["Descripcion_Extra"] != DBNull.Value ? r["Descripcion_Extra"].ToString() : null,
                                        IdRegistro = p.IdRegistro,
                                        EsPagado = (bool)r["Pagado"],
                                        EsVencido = (int)r["Numero_Pago"] <= 0 || (DateTime)r["Fecha_Limite"] <= DateTime.Now.AddDays(30),
                                        UniqueId = r["Id_Cuenta"].ToString()
                                    };

                                    if (item.EsPagado || mostrarFuturos || item.EsVencido)
                                    {
                                        if (!item.EsPagado)
                                        {
                                            // Lógica de selección por defecto
                                            if (pagosSeleccionados != null && pagosSeleccionados.Count > 0) item.EstaSeleccionado = pagosSeleccionados.Contains(item.UniqueId);
                                            else item.EstaSeleccionado = item.EsVencido;

                                            if (item.EstaSeleccionado) subTotalCalculado += item.Monto;
                                        }
                                        p.Pagos.Add(item);
                                    }
                                }
                            }
                        }

                        // Verificar pago total
                        var sqlCheckTotal = @"SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c JOIN ""Eventos_B_Asistentes"" a ON c.""Id_Asistente""=a.""Id_Asistente"" WHERE a.""Id_Asistente""=@asis AND c.""Pagado""=FALSE";
                        using (var cmdCheck = new NpgsqlCommand(sqlCheckTotal, conexion))
                        {
                            cmdCheck.Parameters.AddWithValue("@asis", p.IdRegistro);
                            var resultCheck = await cmdCheck.ExecuteScalarAsync();
                            if (resultCheck != null && Convert.ToInt32(resultCheck) == 0) p.EstaTotalmentePagado = true;
                        }

                        if (p.Pagos.Count > 0) modelo.ListadoPorUsuario.Add(p);
                    }

                    // D.3 Cálculo de Totales (Visual)
                    if (subTotalCalculado > 0)
                    {
                        modelo.SubTotal = subTotalCalculado;

                        // Calculamos siempre la versión con tarjeta para tenerla disponible
                        decimal totalConTarjeta = Funciones.CalcularPagoConComision(subTotalCalculado, _configuration);

                        if (modelo.CobrarComisionExtra)
                        {
                            modelo.ComisionPlataforma = totalConTarjeta - subTotalCalculado;
                            modelo.TotalPagar = totalConTarjeta;
                        }
                        else
                        {
                            modelo.ComisionPlataforma = 0;
                            modelo.TotalPagar = subTotalCalculado;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error Pagar: " + ex.Message);
                MostrarMensaje("Error", "Problema al generar vista de pago.", TipoMensaje.Error);
            }

            using (var conLegales = new NpgsqlConnection(_cadenaConexion))
            {
                await conLegales.OpenAsync();
                (var urlTerminos, var urlPrivacidad) = await Funciones.ObtenerDocumentosLegalesEventosAsync(conLegales);
                ViewBag.UrlTerminos = urlTerminos;
                ViewBag.UrlPrivacidad = urlPrivacidad;
            }

            ViewBag.Token = token;
            ViewBag.IdsSeleccionados = idsAsistentesAConsultar;
            return View(modelo);
        }

        /// <summary>
        /// Este método es el que recibe la confirmación de pago desde Stripe, realiza una re-validación de seguridad (idéntica a la del GET) para asegurar que los datos no fueron manipulados, luego recalcula los totales y line items para evitar confiar en el cliente, y finalmente procesa el pago aplicando la lógica de negocio correspondiente. Es crucial que esta función sea robusta contra manipulaciones, ya que es el punto de entrada final para confirmar un pago.
        /// </summary>
        /// <param name="modeloInput">Es el modelo que viene del formulario de confirmación de pago, que incluye los IDs de pagos seleccionados y el ID del evento. No se debe confiar en esta información sin antes validarla contra la base de datos.</param>
        /// <param name="token">Es el token de invitado, si es que se accede por esa vía. Se debe validar que este token corresponda a un asistente válido y que los IDs de pagos seleccionados realmente pertenezcan a ese asistente (o al usuario logueado en caso de acceso interno) antes de proceder con cualquier cálculo o inserción. Cualquier discrepancia debe resultar en una redirección o error sin procesar el pago.</param>
        /// <returns>Retorna una redirección a la vista de pago con el resultado del proceso, o a la página principal si hay un error de validación grave.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IniciarPagoStripe(ConfirmarPagoViewModel modeloInput, string token)
        {
            // -----------------------------------------------------------------------
            // A. RE-VALIDACIÓN DE SEGURIDAD (Idéntica al GET para asegurar consistencia)
            // -----------------------------------------------------------------------
            var idsAsistentesAConsultar = new List<int>();
            if (TempData["IdsSeguros"] != null)
            {
                try
                {
                    string jsonIds = TempData["IdsSeguros"].ToString();
                    idsAsistentesAConsultar = System.Text.Json.JsonSerializer.Deserialize<List<int>>(jsonIds);
                    TempData.Keep("IdsSeguros");
                }
                catch { }
            }

            bool esInvitado = !string.IsNullOrEmpty(token);
            int idUsuarioActual = 0;
            int idUserOwner = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    bool permiteTarjeta = true;
                    bool cobrarComisionExtra = true;
                    string sqlMetodo = @"SELECT ""Permitir_Pago_Tarjeta"", ""Cobrar_Comision_Extra"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @idEv";
                    using (var cmdMetodo = new NpgsqlCommand(sqlMetodo, conexion))
                    {
                        cmdMetodo.Parameters.AddWithValue("@idEv", modeloInput.IdEvento);
                        using (var reader = await cmdMetodo.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                permiteTarjeta = reader["Permitir_Pago_Tarjeta"] != DBNull.Value ? (bool)reader["Permitir_Pago_Tarjeta"] : true;
                                cobrarComisionExtra = reader["Cobrar_Comision_Extra"] != DBNull.Value ? (bool)reader["Cobrar_Comision_Extra"] : true;
                            }
                        }
                    }

                    if (!permiteTarjeta)
                    {
                        MostrarMensaje("Pago no autorizado", "El pago con tarjeta no está habilitado para este evento.", TipoMensaje.Error);
                        return RedirectToAction("Pagar", new { token = token });
                    }

                    if (esInvitado)
                    {
                        string sqlTok = @"SELECT b.""Id_Asistente"", r.""Id_Evento"", r.""Id_Usuario"" 
                                  FROM ""Eventos_B_Asistentes"" b 
                                  JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                  WHERE b.""Token_Pago_Externo"" = @tok";
                        using (var cmd = new NpgsqlCommand(sqlTok, conexion))
                        {
                            cmd.Parameters.AddWithValue("@tok", token);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    idsAsistentesAConsultar.Clear();
                                    idsAsistentesAConsultar.Add((int)r["Id_Asistente"]);
                                    modeloInput.IdEvento = (int)r["Id_Evento"];
                                    idUserOwner = (int)r["Id_Usuario"];
                                }
                                else
                                {
                                    MostrarMensaje("Error", "No se ha podido identificar el usuario", TipoMensaje.Alerta);
                                    return RedirectToAction("Index", "Home");
                                }
                            }
                        }
                    }
                    else
                    {
                        if (!User.Identity.IsAuthenticated) return Challenge();
                        idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
                        idUserOwner = idUsuarioActual;

                        string sqlVal = @"SELECT b.""Id_Asistente"" FROM ""Eventos_B_Asistentes"" b 
                                  JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                  WHERE r.""Id_Usuario"" = @uid AND b.""Id_Asistente"" = ANY(@ids)";
                        var idsValidos = new List<int>();
                        using (var cmd = new NpgsqlCommand(sqlVal, conexion))
                        {
                            cmd.Parameters.AddWithValue("@uid", idUsuarioActual);
                            cmd.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync()) idsValidos.Add((int)r["Id_Asistente"]);
                            }
                        }
                        idsAsistentesAConsultar = idsValidos;
                    }

                    if (!idsAsistentesAConsultar.Any())
                    {
                        MostrarMensaje("Error", "No ha seleccionado ningun usuario.", TipoMensaje.Alerta);
                        return RedirectToAction("Pagar", new { token = token });
                    }


                    //Bloqueo de seguridad por si quieren usar un cupón sin estar autenticados (solo en modo invitado, en modo interno se asume que el usuario ya tiene cuenta y sesión)
                    string codigoCuponInput = modeloInput.CodigoCuponAplicado;

                    // Si trae texto en el cupón Y NO está autenticado -> CANCELAR TODO
                    if (!string.IsNullOrWhiteSpace(codigoCuponInput) && !User.Identity.IsAuthenticated)
                    {
                        MostrarMensaje("Acceso Requerido", "Para usar cupones debes iniciar sesión o crear una cuenta.", TipoMensaje.Alerta);

                        // Lo regresamos a la vista Pagar (abortamos el cobro)
                        return RedirectToAction("Pagar", new { token = token });
                    }

                    // -----------------------------------------------------------------------
                    // B. RE-CÁLCULO DE TOTALES Y LINE ITEMS 
                    // -----------------------------------------------------------------------
                    var lineItemsStripe = new List<Stripe.Checkout.SessionLineItemOptions>();
                    decimal subTotalCalculado = 0;
                    var idsCuentasPagar = new List<int>();

                    // Re-consultamos nombres para Stripe
                    var mapaNombres = new Dictionary<int, string>();
                    string sqlNombres = @"SELECT ""Id_Asistente"", ""Nombre_Completo"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                    using (var cmd = new NpgsqlCommand(sqlNombres, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync()) mapaNombres[(int)r["Id_Asistente"]] = r["Nombre_Completo"].ToString();
                        }
                    }

                    foreach (var idAsistente in idsAsistentesAConsultar)
                    {
                        string nombreAsistente = mapaNombres.ContainsKey(idAsistente) ? mapaNombres[idAsistente] : "Asistente";

                        // Misma query de deuda que en el GET
                        string sqlDeuda = @"SELECT c.""Id_Cuenta"", c.""Monto_Pagar"", c.""Pagado"", 
                                    CASE 
                                        WHEN cal.""Numero_Pago"" <= -10000 AND pe.""Cantidad"" > 1 THEN 
                                            cal.""Nombre_Concepto"" || ' (Unidad ' || ROW_NUMBER() OVER(PARTITION BY c.""Id_Asistente"", c.""Id_Calendario"" ORDER BY c.""Id_Cuenta"") || ' de ' || pe.""Cantidad"" || ')'
                                        ELSE cal.""Nombre_Concepto"" 
                                    END AS ""Nombre_Concepto"", 
                                    cal.""Numero_Pago""
                                    FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario"" 
                                    LEFT JOIN ""Eventos_Asistentes_ProductosExtra"" pe 
                                           ON pe.""Id_Asistente"" = c.""Id_Asistente"" 
                                          AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"")
                                    WHERE c.""Id_Asistente"" = @asis AND c.""Pagado"" = FALSE";

                        using (var cmd = new NpgsqlCommand(sqlDeuda, conexion))
                        {
                            cmd.Parameters.AddWithValue("@asis", idAsistente);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    string uniqueId = r["Id_Cuenta"].ToString();

                                    // Solo procesamos lo que el usuario seleccionó en el form
                                    if (modeloInput.IdsPagosSeleccionados.Contains(uniqueId))
                                    {
                                        decimal monto = (decimal)r["Monto_Pagar"];
                                        subTotalCalculado += monto;
                                        idsCuentasPagar.Add((int)r["Id_Cuenta"]);

                                        int np = (int)r["Numero_Pago"];
                                        string descItem = np < 0 ? "Producto Extra / Adicional" : $"Pago #{np}";

                                        // Generar item para Stripe
                                        lineItemsStripe.Add(new Stripe.Checkout.SessionLineItemOptions
                                        {
                                            PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                                            {
                                                UnitAmountDecimal = monto * 100,
                                                Currency = "mxn",
                                                ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions
                                                {
                                                    Name = $"{nombreAsistente} - {r["Nombre_Concepto"]}",
                                                    Description = descItem
                                                }
                                            },
                                            Quantity = 1
                                        });
                                    }
                                }
                            }
                        }
                    }

                    if (subTotalCalculado <= 0)
                    {
                        MostrarMensaje("Error", "La cantidad a pagar debe ser mayor a cero", TipoMensaje.Alerta);
                        return RedirectToAction("Pagar", new { token = token });
                    }

                    // Calcular Comisión y Total Final
                    decimal totalFinal = subTotalCalculado;
                    decimal comision = 0;
                    if (cobrarComisionExtra)
                    {
                        totalFinal = Funciones.CalcularPagoConComision(subTotalCalculado, _configuration);
                        comision = totalFinal - subTotalCalculado;
                    }

                    if (comision > 0)
                    {
                        lineItemsStripe.Add(new Stripe.Checkout.SessionLineItemOptions
                        {
                            PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                            {
                                UnitAmountDecimal = comision * 100,
                                Currency = "mxn",
                                ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions { Name = "Gastos de Gestión" }
                            },
                            Quantity = 1
                        });
                    }

                    // -----------------------------------------------------------------------
                    // C. TRANSACCIÓN ATÓMICA: BD + STRIPE SESSION
                    // -----------------------------------------------------------------------
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // --- 1. BLOQUEO DE SEGURIDAD CONTRA SOBREVENTAS (STRIPE) ---
                            var disp = await ConsultarDisponibilidadEvento(modeloInput.IdEvento, conexion, trans, true, idsAsistentesAConsultar);

                            if (disp != null)
                            {
                                // REGLA DE NEGOCIO: Si hay modalidades, se desactiva el cupo global y se valida estrictamente cada modalidad del grupo que paga.
                                if (disp.Modalidades != null && disp.Modalidades.Any())
                                {
                                    // Consultamos a qué modalidad pertenece cada uno de los asistentes que se van a liquidar
                                    string sqlSubAsis = @"SELECT ""Id_Subtipo"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                                    var subtiposPagar = new List<int>();
                                    using (var cmdSubAsis = new NpgsqlCommand(sqlSubAsis, conexion, trans))
                                    {
                                        cmdSubAsis.Parameters.AddWithValue("@ids", idsAsistentesAConsultar);
                                        using (var rSub = await cmdSubAsis.ExecuteReaderAsync())
                                        {
                                            while (await rSub.ReadAsync())
                                            {
                                                if (rSub[0] != DBNull.Value) subtiposPagar.Add(Convert.ToInt32(rSub[0]));
                                            }
                                        }
                                    }

                                    var conteoPorModalidad = subtiposPagar.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

                                    foreach (var kvp in conteoPorModalidad)
                                    {
                                        var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == kvp.Key);
                                        if (modal != null && modal.LugaresDisponibles < kvp.Value)
                                        {
                                            throw new Exception($"Tu tiempo de reserva de 30 minutos expiró y los lugares para la modalidad '{modal.NombreModalidad}' ya fueron ocupados por alguien más.");
                                        }
                                    }
                                }
                                else
                                {
                                    // Si NO tiene modalidades, el cupo global hace su función normal
                                    if (disp.CupoMaximoEvento > 0 && disp.DisponiblesGlobal < idsAsistentesAConsultar.Count)
                                    {
                                        throw new Exception("Tu tiempo de reserva de 30 minutos expiró y los lugares globales ya fueron ocupados por alguien más.");
                                    }
                                }
                            }

                            decimal descuentoAplicado = 0;
                            int? idCuponAplicado = null;
                            string codigoGuardar = null;

                            if (!string.IsNullOrEmpty(codigoCuponInput))
                            {
                                // Validamos que exista un usuario real operando
                                if (idUserOwner <= 0)
                                {
                                    MostrarMensaje("Error", "No hemos podido identificar a quien registró al(los) usuario(s)", TipoMensaje.Alerta);
                                    return RedirectToAction("Pagar", new { token = token });
                                }

                                string sqlCupon = @"SELECT c.*,
        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalLista"",
        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"" AND u.""Id_Usuario"" = @uid) as ""TengoPermiso""
        FROM ""Sist_Cupones"" c 
        WHERE UPPER(c.""Codigo"") = UPPER(@cod) 
        AND c.""Activo"" = TRUE 
        AND NOW() BETWEEN c.""Fecha_Inicio"" AND c.""Fecha_Fin"" 
        FOR UPDATE";

                                using (var cmdC = new NpgsqlCommand(sqlCupon, conexion, trans))
                                {
                                    cmdC.Parameters.AddWithValue("@cod", codigoCuponInput.Trim());
                                    cmdC.Parameters.AddWithValue("@uid", idUserOwner);

                                    using (var rC = await cmdC.ExecuteReaderAsync())
                                    {
                                        if (await rC.ReadAsync())
                                        {
                                            int limite = (int)rC["Limite_Usos"];
                                            int usados = (int)rC["Conteo_Usados"];
                                            decimal minimo = (decimal)rC["Monto_Minimo_Compra"];

                                            // Lógica de alcance
                                            bool aplicaEventos = (bool)rC["Aplica_Eventos"];
                                            int? idEventoRestringido = rC["Id_Evento_Restringido"] as int?;

                                            // Lógica de Lista de Usuarios (NUEVA)
                                            bool tieneLista = Convert.ToInt64(rC["TotalLista"]) > 0;
                                            bool tengoPermiso = Convert.ToInt64(rC["TengoPermiso"]) > 0;

                                            bool stockOk = usados < limite;
                                            bool montoOk = subTotalCalculado >= minimo;
                                            bool eventoOk = !aplicaEventos || !idEventoRestringido.HasValue || idEventoRestringido.Value == modeloInput.IdEvento;

                                            bool usuarioOk = true;
                                            if (tieneLista && !tengoPermiso) usuarioOk = false;

                                            if (stockOk && montoOk && eventoOk && usuarioOk)
                                            {
                                                int tipo = (int)rC["Tipo_Descuento"];
                                                decimal valor = (decimal)rC["Valor"];
                                                decimal? tope = rC["Tope_Maximo_Descuento"] as decimal?;

                                                descuentoAplicado = Funciones.CalcularMontoDescuento(subTotalCalculado, tipo, valor, tope);
                                                idCuponAplicado = (int)rC["Id_Cupon"];
                                                codigoGuardar = codigoCuponInput.ToUpper();
                                            }
                                        }
                                    }
                                }

                                if (idCuponAplicado.HasValue)
                                {
                                    await new NpgsqlCommand($"UPDATE \"Sist_Cupones\" SET \"Conteo_Usados\" = \"Conteo_Usados\" + 1 WHERE \"Id_Cupon\" = {idCuponAplicado}", conexion, trans).ExecuteNonQueryAsync();
                                }
                                else
                                {
                                    throw new Exception("El cupón ingresado se agotó o ya no es válido. Actualiza la página e intenta sin cupón.");
                                }
                            }

                            decimal totalBaseMenosDescuento = subTotalCalculado - descuentoAplicado;
                            if (totalBaseMenosDescuento < 0) totalBaseMenosDescuento = 0;

                            decimal totalFinalConComision = Funciones.CalcularPagoConComision(totalBaseMenosDescuento, _configuration);
                            decimal comisionStripe = totalFinalConComision - totalBaseMenosDescuento;

                            string sqlD = @"INSERT INTO ""Eventos_D_Transacciones"" 
                                    (""Id_Usuario"", ""Id_Evento"", ""Monto_Total"", ""Estatus_Pago"", ""Fecha_Intento"", ""EsTransferencia"", 
                                     ""Id_Cupon"", ""Codigo_Cupon_Aplicado"", ""Monto_Descuento"")
                                    VALUES (@usr, @ev, @monto, 'pending', NOW(), FALSE, @idC, @codC, @montC) 
                                    RETURNING ""Id_Transaccion""";

                            int idTrx = 0;
                            using (var cmd = new NpgsqlCommand(sqlD, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@usr", idUserOwner);
                                cmd.Parameters.AddWithValue("@ev", modeloInput.IdEvento);
                                cmd.Parameters.AddWithValue("@monto", totalFinalConComision);
                                cmd.Parameters.AddWithValue("@idC", (object)idCuponAplicado ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@codC", (object)codigoGuardar ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@montC", descuentoAplicado);

                                idTrx = (int)await cmd.ExecuteScalarAsync();
                            }

                            if (idCuponAplicado.HasValue && idUserOwner > 0)
                            {
                                string sqlUso = @"INSERT INTO ""Sist_Cupones_Uso"" (""Id_Cupon"", ""Id_Usuario"", ""Id_Transaccion_Evento"", ""Fecha_Uso"") 
                                                    VALUES (@idC, @idU, @idTrx, NOW())";

                                using (var cmdUso = new NpgsqlCommand(sqlUso, conexion, trans))
                                {
                                    cmdUso.Parameters.AddWithValue("@idC", idCuponAplicado);
                                    cmdUso.Parameters.AddWithValue("@idU", idUserOwner);
                                    cmdUso.Parameters.AddWithValue("@idTrx", idTrx);
                                    await cmdUso.ExecuteNonQueryAsync();
                                }
                            }

                            string referenciaUnica = $"EVT_{idTrx}_{Guid.NewGuid().ToString("N").Substring(0, 5).ToUpper()}";
                            await new NpgsqlCommand($"UPDATE \"Eventos_D_Transacciones\" SET \"External_Reference\"='{referenciaUnica}' WHERE \"Id_Transaccion\"={idTrx}", conexion, trans).ExecuteNonQueryAsync();

                            string sqlE = @"INSERT INTO ""Eventos_E_Pagos_Aplicados"" (""Id_Transaccion"", ""Id_Cuenta"") VALUES (@tr, @cta)";
                            foreach (var idCta in idsCuentasPagar)
                            {
                                using (var cmdE = new NpgsqlCommand(sqlE, conexion, trans))
                                {
                                    cmdE.Parameters.AddWithValue("@tr", idTrx);
                                    cmdE.Parameters.AddWithValue("@cta", idCta);
                                    await cmdE.ExecuteNonQueryAsync();
                                }
                            }

                            string emailCliente = null;
                            if (!esInvitado && idUsuarioActual > 0)
                            {
                                try
                                {
                                    var cmdEmail = new NpgsqlCommand(@"SELECT ""Email"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario"" = @uid", conexion, trans);
                                    cmdEmail.Parameters.AddWithValue("@uid", idUsuarioActual);
                                    var resEmail = await cmdEmail.ExecuteScalarAsync();
                                    if (resEmail != null) emailCliente = resEmail.ToString();
                                }
                                catch { }
                            }

                            string urlRetorno = $"{Request.Scheme}://{Request.Host}/Eventos/Pagar{(esInvitado ? $"/{token}" : "")}";

                            var options = new Stripe.Checkout.SessionCreateOptions
                            {
                                PaymentMethodTypes = new List<string> { "card" },
                                LineItems = lineItemsStripe,
                                Mode = "payment",
                                SuccessUrl = $"{urlRetorno}?session_id={{CHECKOUT_SESSION_ID}}",
                                CancelUrl = urlRetorno,
                                ClientReferenceId = referenciaUnica,
                                CustomerEmail = emailCliente,
                                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                            };

                            if (descuentoAplicado > 0)
                            {
                                var couponOptions = new Stripe.CouponCreateOptions
                                {
                                    AmountOff = (long)(descuentoAplicado * 100),
                                    Currency = "mxn",
                                    Duration = "once",
                                    Name = $"Cupon: {codigoGuardar}"
                                };
                                var couponService = new Stripe.CouponService();
                                var stripeCoupon = await couponService.CreateAsync(couponOptions);

                                options.Discounts = new List<Stripe.Checkout.SessionDiscountOptions>
                                {
                                    new Stripe.Checkout.SessionDiscountOptions { Coupon = stripeCoupon.Id }
                                };
                            }

                            if (lineItemsStripe.Any(x => x.PriceData.ProductData.Name == "Gastos de Gestión"))
                            {
                                lineItemsStripe.RemoveAll(x => x.PriceData.ProductData.Name == "Gastos de Gestión");

                                if (comisionStripe > 0)
                                {
                                    lineItemsStripe.Add(new Stripe.Checkout.SessionLineItemOptions
                                    {
                                        PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                                        {
                                            UnitAmountDecimal = comisionStripe * 100,
                                            Currency = "mxn",
                                            ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions { Name = "Gastos de Gestión" }
                                        },
                                        Quantity = 1
                                    });
                                }
                            }

                            var service = new Stripe.Checkout.SessionService();
                            var session = await service.CreateAsync(options);

                            string sqlUpdRef = @"UPDATE ""Eventos_D_Transacciones"" SET ""Ref_Pasarela"" = @sid WHERE ""Id_Transaccion"" = @id";
                            using (var cmd = new NpgsqlCommand(sqlUpdRef, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@sid", session.Id);
                                cmd.Parameters.AddWithValue("@id", idTrx);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();
                            return Redirect(session.Url);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            Console.WriteLine("Error IniciarPagoStripe: " + ex.Message);
                            MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                            return RedirectToAction("Pagar", new { token = token });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error General POST: " + ex.Message);
                return RedirectToAction("Pagar", new { token = token });
            }
        }

        /// <summary>
        /// Genera el recibo de pago para un asistente específico basado en el token y el ID de la cuenta.
        /// </summary>
        /// <param name="token">Es el token único del asistente.</param>
        /// <param name="idCuenta">Es el ID de la cuenta pagada.</param>
        /// <returns>Es la vista del recibo.</returns>
        [AllowAnonymous]
        [HttpGet("Eventos/Recibo/{token}/{idCuenta}")]
        public async Task<IActionResult> Recibo(string token, int idCuenta)
        {
            if (string.IsNullOrEmpty(token)) return NotFound();

            var modelo = new ReciboViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Consulta blindada: Solo devuelve datos si el TOKEN coincide con el ASISTENTE dueño de la cuenta
                    // Se agregó el JOIN a Eventos_Subtipos para obtener la Modalidad
                    string sql = @"
                SELECT 
                    e.""Titulo"", 
                    b.""Nombre_Completo"", 
                    b.""Id_Asistente"",
                    e.""Id_Encuesta_Requisito"",
                    (SELECT enc.""Clave_Url"" FROM ""Encuestas_Catalogo"" enc WHERE enc.""Id_Encuesta"" = e.""Id_Encuesta_Requisito"") as ""ClaveEncuesta"",
                    COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                              WHERE h.""Id_Encuesta"" = e.""Id_Encuesta_Requisito"" 
                                AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida"",
                    s.""Nombre"" as ""Modalidad"",
                    CASE 
                        WHEN cal.""Numero_Pago"" <= -10000 AND pe.""Cantidad"" > 1 THEN 
                            cal.""Nombre_Concepto"" || ' (Unidad ' || (
                                SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c_sub 
                                WHERE c_sub.""Id_Asistente"" = c.""Id_Asistente"" 
                                  AND c_sub.""Id_Calendario"" = c.""Id_Calendario"" 
                                  AND c_sub.""Id_Cuenta"" <= c.""Id_Cuenta""
                            ) || ' de ' || pe.""Cantidad"" || ')'
                        ELSE cal.""Nombre_Concepto"" 
                    END AS ""Nombre_Concepto"", 
                    cal.""Numero_Pago"", 
                    c.""Monto_Pagar"", 
                    c.""Fecha_Pagado"",
                    d.""Ref_Pasarela"",    
                    d.""Id_Transaccion"",
                    d.""Monto_Descuento"",       -- NUEVO
                    d.""Codigo_Cupon_Aplicado"", -- NUEVO
                    cat.""Descripcion"" AS ""Descripcion_Extra""
                FROM ""Eventos_C_Cuentas_Cobrar"" c
                JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_Asistentes_ProductosExtra"" pe 
                       ON pe.""Id_Asistente"" = c.""Id_Asistente"" 
                      AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"")
                LEFT JOIN ""Eventos_Catalogo_ProductosExtra"" cat
                       ON cat.""Id_ProductoExtra"" = -(cal.""Numero_Pago"" + 10000)
                LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON c.""Id_Cuenta"" = pa.""Id_Cuenta""
                LEFT JOIN ""Eventos_D_Transacciones"" d ON pa.""Id_Transaccion"" = d.""Id_Transaccion""
                WHERE c.""Id_Cuenta"" = @idC 
                  AND b.""Token_Pago_Externo"" = @tok
                  AND c.""Pagado"" = TRUE
                ORDER BY d.""Completado"" DESC, d.""Id_Transaccion"" DESC
                LIMIT 1";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idC", idCuenta);
                        cmd.Parameters.AddWithValue("@tok", token);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                int idEncReqRecibo = r["Id_Encuesta_Requisito"] != DBNull.Value ? Convert.ToInt32(r["Id_Encuesta_Requisito"]) : 0;
                                bool encRespRecibo = (bool)r["EncuestaRespondida"];
                                string claveEncRecibo = r["ClaveEncuesta"]?.ToString();
                                int idAsisRecibo = (int)r["Id_Asistente"];

                                if (idEncReqRecibo > 0 && !encRespRecibo && !string.IsNullOrEmpty(claveEncRecibo))
                                {
                                    return Redirect($"/E/{claveEncRecibo}?asis={idAsisRecibo}&retorno={token}");
                                }

                                modelo.Evento = r["Titulo"].ToString();
                                modelo.Asistente = r["Nombre_Completo"].ToString();
                                modelo.Modalidad = r["Modalidad"] != DBNull.Value ? r["Modalidad"].ToString() : "Entrada General";
                                modelo.Concepto = r["Nombre_Concepto"].ToString();
                                modelo.Descripcion = r["Descripcion_Extra"] != DBNull.Value ? r["Descripcion_Extra"].ToString() : null;
                                modelo.NumeroPago = (int)r["Numero_Pago"];
                                modelo.Monto = (decimal)r["Monto_Pagar"];
                                modelo.FechaPago = r["Fecha_Pagado"] != DBNull.Value ? (DateTime)r["Fecha_Pagado"] : DateTime.Now;

                                // Datos del Cupón
                                modelo.Descuento = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0;
                                modelo.CodigoCupon = r["Codigo_Cupon_Aplicado"]?.ToString();

                                // Preferimos mostrar la referencia de Stripe si existe, si no, la interna
                                string refPasarela = r["Ref_Pasarela"]?.ToString();
                                modelo.Folio = !string.IsNullOrEmpty(refPasarela) ? refPasarela : $"INT-{r["Id_Transaccion"]}";

                                // URL para el QR (Validador ficticio, o puedes apuntar al mismo recibo)
                                // Esto permite que cualquiera que escanee vea este documento original en la web
                                modelo.UrlValidacion = Url.Action("Recibo", "Eventos", new { token = token, idCuenta = idCuenta }, Request.Scheme);
                            }
                            else
                            {
                                return NotFound("Recibo no encontrado o acceso denegado.");
                            }
                        }
                    }
                }
            }
            catch { return BadRequest(); }

            return View(modelo);
        }

        /// <summary>
        /// Genera el recibo general de todos los pagos realizados por un asistente basado en el token.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("Eventos/ReciboGeneral/{token}")]
        public async Task<IActionResult> ReciboGeneral(string token)
        {
            if (string.IsNullOrEmpty(token)) return NotFound();

            var modelo = new ReciboGeneralViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Obtener Id_Asistente
                    int idAsistente = 0;
                    string sqlAsis = @"SELECT ""Id_Asistente"" FROM ""Eventos_B_Asistentes"" WHERE ""Token_Pago_Externo"" = @tok LIMIT 1";
                    using (var cmd = new NpgsqlCommand(sqlAsis, conexion))
                    {
                        cmd.Parameters.AddWithValue("@tok", token);
                        var res = await cmd.ExecuteScalarAsync();
                        if (res != null) idAsistente = (int)res;
                        else return NotFound("Token inválido.");
                    }

                    // 1. Validar Asistente y Token (Se agregó el JOIN a Subtipos)
                    string sqlInfo = @"
                SELECT e.""Titulo"", b.""Nombre_Completo"", s.""Nombre"" as ""Modalidad"",
                       e.""Id_Encuesta_Requisito"",
                       (SELECT enc.""Clave_Url"" FROM ""Encuestas_Catalogo"" enc WHERE enc.""Id_Encuesta"" = e.""Id_Encuesta_Requisito"") as ""ClaveEncuesta"",
                       COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                                 WHERE h.""Id_Encuesta"" = e.""Id_Encuesta_Requisito"" 
                                   AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                WHERE b.""Id_Asistente"" = @id AND b.""Token_Pago_Externo"" = @tok";

                    using (var cmd = new NpgsqlCommand(sqlInfo, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idAsistente);
                        cmd.Parameters.AddWithValue("@tok", token);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                int idEncReqGeneral = r["Id_Encuesta_Requisito"] != DBNull.Value ? Convert.ToInt32(r["Id_Encuesta_Requisito"]) : 0;
                                bool encRespGeneral = (bool)r["EncuestaRespondida"];
                                string claveEncGeneral = r["ClaveEncuesta"]?.ToString();

                                if (idEncReqGeneral > 0 && !encRespGeneral && !string.IsNullOrEmpty(claveEncGeneral))
                                {
                                    return Redirect($"/E/{claveEncGeneral}?asis={idAsistente}&retorno={token}");
                                }

                                modelo.Evento = r["Titulo"].ToString();
                                modelo.Asistente = r["Nombre_Completo"].ToString();
                                modelo.Modalidad = r["Modalidad"] != DBNull.Value ? r["Modalidad"].ToString() : "Entrada General";
                            }
                            else return NotFound("No se encontró el registro.");
                        }
                    }

                    // 2. OBTENER DESGLOSE (CORREGIDO PARA EVITAR DOBLE SUMA DE DESCUENTOS)
                    // Agregamos d."Id_Transaccion" al Group By para poder identificar pagos únicos
                    string sqlPagos = @"
                SELECT 
                    CASE 
                        WHEN cal.""Numero_Pago"" <= -10000 AND pe.""Cantidad"" > 1 THEN 
                            cal.""Nombre_Concepto"" || ' (Unidad ' || (
                                SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c_sub 
                                WHERE c_sub.""Id_Asistente"" = c.""Id_Asistente"" 
                                  AND c_sub.""Id_Calendario"" = c.""Id_Calendario"" 
                                  AND c_sub.""Id_Cuenta"" <= c.""Id_Cuenta""
                            ) || ' de ' || pe.""Cantidad"" || ')'
                        ELSE cal.""Nombre_Concepto"" 
                    END AS ""Nombre_Concepto"", 
                    cal.""Numero_Pago"", 
                    c.""Monto_Pagar"", 
                    c.""Fecha_Pagado"",
                    MAX(d.""Ref_Pasarela"") as ""Ref_Pasarela"",
                    MAX(d.""Monto_Descuento"") as ""Descuento"",
                    MAX(d.""Id_Transaccion"") as ""Id_Transaccion"", -- <--- NECESARIO PARA FILTRAR
                    MAX(cat.""Descripcion"") as ""Descripcion_Extra""
                FROM ""Eventos_C_Cuentas_Cobrar"" c
                JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                LEFT JOIN ""Eventos_Asistentes_ProductosExtra"" pe 
                       ON pe.""Id_Asistente"" = c.""Id_Asistente"" 
                      AND cal.""Numero_Pago"" = -(10000 + pe.""Id_ProductoExtra"")
                LEFT JOIN ""Eventos_Catalogo_ProductosExtra"" cat
                       ON cat.""Id_ProductoExtra"" = -(cal.""Numero_Pago"" + 10000)
                LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON c.""Id_Cuenta"" = pa.""Id_Cuenta""
                LEFT JOIN ""Eventos_D_Transacciones"" d ON pa.""Id_Transaccion"" = d.""Id_Transaccion"" AND d.""Completado"" = TRUE
                WHERE c.""Id_Asistente"" = @id AND c.""Pagado"" = TRUE
                GROUP BY c.""Id_Cuenta"", cal.""Nombre_Concepto"", cal.""Numero_Pago"", c.""Monto_Pagar"", c.""Fecha_Pagado"", pe.""Cantidad""
                ORDER BY CASE WHEN cal.""Numero_Pago"" > 0 THEN cal.""Numero_Pago"" ELSE 99999 + ABS(cal.""Numero_Pago"") END ASC";

                    // Usamos un HashSet para recordar qué transacciones ya sumamos al descuento
                    var transaccionesProcesadas = new HashSet<int>();

                    using (var cmd = new NpgsqlCommand(sqlPagos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idAsistente);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string refPasarela = r["Ref_Pasarela"] != DBNull.Value ? r["Ref_Pasarela"].ToString() : "MANUAL/EFECTIVO";
                                string concepto = r["Nombre_Concepto"].ToString();
                                string descExtra = r["Descripcion_Extra"] != DBNull.Value ? r["Descripcion_Extra"].ToString() : null;
                                if (string.IsNullOrEmpty(concepto)) concepto = $"Pago #{r["Numero_Pago"]}";

                                int idTrx = r["Id_Transaccion"] != DBNull.Value ? (int)r["Id_Transaccion"] : 0;
                                decimal desc = r["Descuento"] != DBNull.Value ? (decimal)r["Descuento"] : 0;

                                // --- CORRECCIÓN MATEMÁTICA ---
                                // Solo sumamos el descuento si NO hemos procesado esta transacción antes
                                if (idTrx > 0 && desc > 0 && !transaccionesProcesadas.Contains(idTrx))
                                {
                                    modelo.TotalDescuentos += desc;
                                    transaccionesProcesadas.Add(idTrx);
                                }
                                // -----------------------------

                                modelo.Desglose.Add(new DetallePagoRecibo
                                {
                                    Concepto = concepto,
                                    Descripcion = descExtra,
                                    Monto = (decimal)r["Monto_Pagar"], // Monto BRUTO del concepto
                                    Fecha = r["Fecha_Pagado"] != DBNull.Value ? (DateTime)r["Fecha_Pagado"] : DateTime.Now,
                                    Referencia = refPasarela
                                });
                            }
                        }
                    }

                    if (modelo.Desglose.Count == 0) return NotFound("No hay pagos completados.");

                    // 3. Totales
                    // Total Pagado = (Suma de Conceptos Brutos) - (Total Descuentos Únicos)
                    decimal sumaBruta = modelo.Desglose.Sum(x => x.Monto);
                    modelo.TotalPagado = sumaBruta - modelo.TotalDescuentos;

                    // Validación de seguridad visual: El pagado nunca puede ser menor a 0
                    if (modelo.TotalPagado < 0) modelo.TotalPagado = 0;

                    modelo.FechaUltimoPago = modelo.Desglose.Max(x => x.Fecha);
                    modelo.FolioGeneral = $"FULL-{idAsistente}-{modelo.FechaUltimoPago:yyMMdd}";
                    modelo.UrlValidacion = Url.Action("ReciboGeneral", "Eventos", new { token = token }, Request.Scheme);
                }
            }
            catch (Exception ex) { return BadRequest("Error: " + ex.Message); }

            return View(modelo);
        }

        /// <summary>
        /// Actualiza la información de un asistente existente.
        /// </summary>
        /// <param name="form">Es el modelo con los datos del asistente a actualizar.</param>
        /// <returns>Es la redirección a la vista de registro del evento.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarAsistente(AsistenteEdicionViewModel form)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string sIdEventoEncriptado = "";
            try
            {
                int IdEvento = form.Id_Evento;
                sIdEventoEncriptado = Funciones.EncriptarId(IdEvento);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (await ValidarEventoBloqueado(form.Id_Evento, conexion))
                    { 
                        MostrarMensaje("Evento Bloqueado", "Por logística, el evento se encuentra bloqueado y ya no permite modificaciones a los registros.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
                    }

                    //Valida que el asistente pertenezca al usuario
                    string sqlCheck = @"SELECT count(*) FROM ""Eventos_B_Asistentes"" b 
                                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                        WHERE b.""Id_Asistente"" = @idA AND r.""Id_Usuario"" = @uid";
                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idA", form.Id_Asistente); cmd.Parameters.AddWithValue("@uid", idUser);
                        if ((long)await cmd.ExecuteScalarAsync() == 0)
                        {
                            MostrarMensaje("Error", "No puedes editar a éste usuario", TipoMensaje.Alerta);
                            return RedirectToAction("Registro", new { sid = sIdEventoEncriptado }); 
                        }
                    }
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        string sqlUpdB = @"UPDATE ""Eventos_B_Asistentes"" SET ""Nombre_Completo"" = @nom, ""Etiqueta_Grupo"" = @grupo, ""Edad"" = @edad, ""Genero"" = @gen WHERE ""Id_Asistente"" = @idA";
                        using (var cmd = new NpgsqlCommand(sqlUpdB, conexion, trans))
                        {
                            cmd.Parameters.AddWithValue("@nom", form.NombreCompleto.Trim());
                            cmd.Parameters.AddWithValue("@grupo", string.IsNullOrWhiteSpace(form.EtiquetaGrupo) ? "General" : form.EtiquetaGrupo);
                            cmd.Parameters.AddWithValue("@edad", form.Edad);
                            cmd.Parameters.AddWithValue("@gen", form.Genero);
                            cmd.Parameters.AddWithValue("@idA", form.Id_Asistente);
                            await cmd.ExecuteNonQueryAsync();
                        }
                        await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\"={form.Id_Asistente}", conexion, trans).ExecuteNonQueryAsync();
                        if (form.Respuestas != null)
                        {
                            string sqlResp = @"INSERT INTO ""Eventos_Respuestas"" (""Id_Asistente"", ""Id_Pregunta"", ""Valor_Respuesta"") VALUES (@asis, @preg, @val)";
                            foreach (var resp in form.Respuestas)
                            {
                                if (!string.IsNullOrWhiteSpace(resp.Valor))
                                {
                                    using (var cmdR = new NpgsqlCommand(sqlResp, conexion, trans))
                                    {
                                        cmdR.Parameters.AddWithValue("@asis", form.Id_Asistente); cmdR.Parameters.AddWithValue("@preg", resp.Id_Pregunta); cmdR.Parameters.AddWithValue("@val", resp.Valor); await cmdR.ExecuteNonQueryAsync();
                                    }
                                }
                            }
                        }

                        await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                            Parametros.AccionesBitacora.Editar,
                            $"Editó datos posteriores del asistente ID {form.Id_Asistente}",
                            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                        await trans.CommitAsync();
                        MostrarMensaje("Actualizado", "Datos modificados.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
        }

        /// <summary>
        /// Verifica los pagos pendientes para un asistente específico y aplica los pagos exitosos.
        /// </summary>
        /// <param name="idAsistente">Es el ID del asistente a verificar.</param>
        /// <returns>Retorna una tarea asincrónica.</returns>
        private async Task VerificarPagosPorAsistente(int idAsistente)
        {
            try
            {
                var pendientes = new List<dynamic>();

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // Traemos External_Reference
                    string sql = @"
                SELECT DISTINCT d.""Id_Transaccion"", d.""Ref_Pasarela"", d.""External_Reference""
                FROM ""Eventos_D_Transacciones"" d
                JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion"" = e.""Id_Transaccion""
                JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta""
                WHERE c.""Id_Asistente"" = @idAsis 
                AND d.""Completado"" = FALSE
                AND d.""Ref_Pasarela"" LIKE 'cs_%'";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@idAsis", idAsistente);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                pendientes.Add(new
                                {
                                    IdTrx = (int)r["Id_Transaccion"],
                                    SessionId = r["Ref_Pasarela"].ToString(),
                                    RefEsperada = r["External_Reference"]?.ToString()
                                });
                            }
                        }
                    }
                }

                if (pendientes.Count > 0)
                {
                    var service = new Stripe.Checkout.SessionService();

                    foreach (var p in pendientes)
                    {
                        try
                        {
                            var session = await service.GetAsync(p.SessionId);

                            if (session.PaymentStatus == "paid")
                            {
                                // SEGURIDAD: Comparación estricta
                                if (p.RefEsperada != null && session.ClientReferenceId == p.RefEsperada)
                                {
                                    await AplicarPagoExitoso(p.IdTrx, session.PaymentIntentId ?? session.Id);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error polling invitado: {ex.Message}"); }
        }

        /// <summary>
        /// Elimina un asistente específico de un evento, validando permisos y pagos asociados.
        /// </summary>
        /// <param name="idAsistente">Es el ID del asistente a eliminar.</param>
        /// <returns>Es la redirección a la vista de registro del evento.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarAsistente(int idAsistente)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            int idEvento = 0;
            string sIdEventoEncriptado = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Verificar que el asistente pertenezca al usuario (Tabla A y B)
                    string sqlCheck = @"SELECT r.""Id_Registro"", r.""Id_Evento"" 
                                FROM ""Eventos_B_Asistentes"" b
                                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                                WHERE b.""Id_Asistente"" = @idA AND r.""Id_Usuario"" = @uid";

                    int idRegistro = 0;
                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idA", idAsistente);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync()) { idRegistro = (int)r[0]; idEvento = (int)r[1]; }
                            else { MostrarMensaje("Error", "No tienes permiso para eliminar este registro.", TipoMensaje.Error); return RedirectToAction("Index"); }
                        }
                    }

                    sIdEventoEncriptado = Funciones.EncriptarId(idEvento);

                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    { // Usa el ID correspondiente al método
                        MostrarMensaje("Evento Bloqueado", "Por logística, el evento se encuentra bloqueado y ya no permite modificaciones a los registros.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
                    }


                    // 2. Validar Pagos Completados
                    long pagados = (long)await new NpgsqlCommand(
                        $"SELECT COUNT(*) FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\"={idAsistente} AND \"Pagado\"=TRUE",
                        conexion).ExecuteScalarAsync();

                    if (pagados > 0)
                    {
                        MostrarMensaje("Bloqueado", "No puedes eliminar a alguien que ya tiene pagos completados.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
                    }

                    // 3. Validar Trámites y Pendientes Recientes (REGLA DE LOS 30 MINUTOS)
                    string sqlEstado = @"SELECT 
                (SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
                    JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" 
                    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" 
                    WHERE c.""Id_Asistente""=@idA AND d.""Estatus_Pago"" IN ('waiting_proof', 'review')) as ""EnTramite"",
                (SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
                    JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion""=e.""Id_Transaccion"" 
                    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta""=c.""Id_Cuenta"" 
                    WHERE c.""Id_Asistente""=@idA AND d.""Estatus_Pago"" = 'pending' AND d.""Fecha_Intento"" >= (NOW() - INTERVAL '30 minutes')) as ""PendientesRecientes""";

                    long enTramite = 0;
                    long pendientesRecientes = 0;

                    using (var cmdSt = new NpgsqlCommand(sqlEstado, conexion))
                    {
                        cmdSt.Parameters.AddWithValue("@idA", idAsistente);
                        using (var r = await cmdSt.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                enTramite = (long)r["EnTramite"];
                                pendientesRecientes = (long)r["PendientesRecientes"];
                            }
                        }
                    }

                    if (enTramite > 0)
                    {
                        MostrarMensaje("Bloqueado", "No puedes eliminar a este asistente porque tiene un pago en revisión o pendiente de comprobante.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
                    }

                    if (pendientesRecientes > 0)
                    {
                        MostrarMensaje("Bloqueado", "Este asistente tiene un intento de pago en proceso. Por seguridad, espera 30 minutos desde su último intento para poder eliminarlo.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
                    }

                    // 4. BORRADO SEGURO (CON TRANSACCIÓN ATÓMICA)
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. Limpiar intentos de pago abandonados (> 30 mins) para evitar violaciones de llave foránea (Eventos_E_Pagos_Aplicados)
                            string sqlDelE = @"DELETE FROM ""Eventos_E_Pagos_Aplicados"" 
                                       WHERE ""Id_Cuenta"" IN (SELECT ""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA)";
                            using (var cmdDelE = new NpgsqlCommand(sqlDelE, conexion, trans))
                            {
                                cmdDelE.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelE.ExecuteNonQueryAsync();
                            }

                            // B. Borrar Cuentas / Deudas (C)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // C. Borrar Respuestas a las preguntas y encuesta resuelta
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            string sqlDelEnc = @"
                                DELETE FROM ""Encuestas_Respuestas_Detalle"" 
                                WHERE ""Id_Respuesta"" IN (SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" WHERE ""Id_Asistente_Evento"" = @idA);
                                DELETE FROM ""Encuestas_Respuestas_Header"" 
                                WHERE ""Id_Asistente_Evento"" = @idA;";
                            using (var cmdDelEnc = new NpgsqlCommand(sqlDelEnc, conexion, trans))
                            {
                                cmdDelEnc.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelEnc.ExecuteNonQueryAsync();
                            }

                            // D. Borrar Asignaciones de Alojamiento y Prioridades
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_I_Alojamiento_Asignaciones\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_J_Prioridad_Alojamiento\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // E. Borrar Asistente (B)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Asistentes_ProductosExtra\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_B_Asistentes\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // E. Limpiar Registro Padre (A) si quedó vacío (Para no dejar basurita en la DB)
                            long quedan = (long)await new NpgsqlCommand($"SELECT COUNT(*) FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\"={idRegistro}", conexion, trans).ExecuteScalarAsync();
                            if (quedan == 0) await new NpgsqlCommand($"DELETE FROM \"Eventos_A_Registros\" WHERE \"Id_Registro\"={idRegistro}", conexion, trans).ExecuteNonQueryAsync();

                            // F. Bitácora
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                                Parametros.AccionesBitacora.Borrar,
                                $"Eliminó asistente ID {idAsistente} del Evento #{idEvento}",
                                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                            await trans.CommitAsync();
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw; // Re-lanzamos al catch principal para que lo capture
                        }
                    }

                    MostrarMensaje("Eliminado", "Registro eliminado correctamente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Registro", new { sid = sIdEventoEncriptado });
        }

        /// <summary>
        /// Elimina un evento completo, validando permisos y pagos asociados.
        /// </summary>
        /// <param name="id">Es el ID del evento a eliminar.</param>
        /// <returns>Es la redirección a la vista de índice de eventos.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permisos de borrado en ésta página", TipoMensaje.Alerta); 
                return RedirectToAction("Index"); 
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // VALIDAR PAGOS EXISTENTES (En Tabla C)
                    long pagos = (long)await new NpgsqlCommand(
                        $"SELECT COUNT(*) FROM \"Eventos_C_Cuentas_Cobrar\" c JOIN \"Eventos_B_Asistentes\" b ON c.\"Id_Asistente\"=b.\"Id_Asistente\" JOIN \"Eventos_A_Registros\" r ON b.\"Id_Registro\" = r.\"Id_Registro\" WHERE r.\"Id_Evento\"={id} AND c.\"Pagado\"=TRUE",
                        conexion).ExecuteScalarAsync();

                    if (pagos > 0)
                    {
                        MostrarMensaje("Error", "No se puede eliminar un evento con pagos confirmados.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Borrar Relaciones (E)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_E_Pagos_Aplicados\" WHERE \"Id_Cuenta\" IN (SELECT \"Id_Cuenta\" FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id})))", conexion, trans).ExecuteNonQueryAsync();

                            // 2. Borrar Cuentas (C)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id}))", conexion, trans).ExecuteNonQueryAsync();

                            // 3. Borrar Transacciones (D)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_D_Transacciones\" WHERE \"Id_Evento\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            // 4. Borrar Respuestas
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id}))", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Encuestas_Respuestas_Detalle\" WHERE \"Id_Respuesta\" IN (SELECT \"Id_Respuesta\" FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Asistente_Evento\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id})))", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Asistente_Evento\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id}))", conexion, trans).ExecuteNonQueryAsync();

                            // 5. Borrar Asistentes (B)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Asistentes_ProductosExtra\" WHERE \"Id_Asistente\" IN (SELECT \"Id_Asistente\" FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id}))", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" IN (SELECT \"Id_Registro\" FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id})", conexion, trans).ExecuteNonQueryAsync();

                            // 6. Borrar Registros (A)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_A_Registros\" WHERE \"Id_Evento\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            // 7. Borrar Configuración (Calendario y Preguntas)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Calendario\" WHERE \"Id_Evento\"={id}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Preguntas\" WHERE \"Id_Evento\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            // 8. Borrar Evento
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Catalogo\" WHERE \"Id_Evento\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            // --- BITÁCORA ---
                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Evento #{id} ELIMINADO permanentemente.", ip, trans);

                            await trans.CommitAsync();

                            try
                            {
                                string folderPath = $"{sAmbiente}/Imágenes/Eventos/{id}";
                                await _cloudinary.DeleteResourcesByPrefixAsync($"{folderPath}/");
                                await _cloudinary.DeleteFolderAsync(folderPath);
                            }
                            catch { /* Ignorar si falla, BD ya limpia */ }
                            MostrarMensaje("Eliminado", "Evento borrado.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error al eliminar", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }

        /// <summary>
        /// AJAX: Obtiene las modalidades de un evento para el selector de vinculación.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetSubtiposEvento(int idEvento)
        {
            var lista = new List<dynamic>();
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    string sql = @"SELECT ""Id_Subtipo"", ""Nombre"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento"" = @id AND ""Activo"" = TRUE ORDER BY ""Costo"" ASC, ""Nombre"" ASC";
                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                lista.Add(new { id = (int)r["Id_Subtipo"], nombre = r["Nombre"].ToString() });
                            }
                        }
                    }
                }
            }
            catch { }
            return Json(lista);
        }

        /// <summary>
        /// Muestra el editor para crear o modificar un evento.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> Editor(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            // Defaults
            ViewBag.Titulo = ""; ViewBag.Descripcion = ""; ViewBag.Fecha_Inicio = DateTime.Now.Date.AddDays(1).AddHours(9); ViewBag.Fecha_Fin = DateTime.Now.Date.AddDays(1).AddHours(13);
            ViewBag.Costo_Entrada = 0.00m; ViewBag.Requiere_Registro = false; ViewBag.Tipo_Registro = 1; ViewBag.Cupo_Maximo = 0; ViewBag.Imagen_Url = "";
            ViewBag.Es_Destacado = false; ViewBag.Activo = true; ViewBag.Permitir_Pago = false; ViewBag.Permitir_Pagos_Parciales = false;
            ViewBag.CalendarioPagos = new List<CalendarioPagoItem>(); ViewBag.Preguntas = new List<PreguntaConfigitem>(); ViewBag.TienePagos = false;

            ViewBag.Es_Privado = false; 
            ViewBag.ClaveCuenta = "DEFAULT";
            ViewBag.Subtipos = new List<SubtipoEventoItem>();
            ViewBag.Galeria = new List<FotoGaleriaItem>();
            ViewBag.Inscriptores = new List<InscriptorItem>();
            ViewBag.ListaBancos = GlobalController.DatosBancarios.ObtenerCatalogoCuentas();

            ViewBag.Usa_Cuenta_Personalizada = false;
            ViewBag.Banco_Custom = "";
            ViewBag.Titular_Custom = "";
            ViewBag.Clabe_Custom = "";
            ViewBag.Cuenta_Custom = "";

            // Defaults de Vinculación
            ViewBag.Id_Evento_Vinculado = 0;
            ViewBag.Subtipos_Vinculados_Acceso = "";

            var inscriptoresConHistorial = new List<int>();
            var todosUsuarios = new List<dynamic>();

            try
            {
                var id = Funciones.DesencriptarId(sid);
                ViewBag.Id_Evento = id;
                ViewBag.Id_Encriptado_Evento = sid;

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // CARGAR EVENTOS VINCULABLES
                    var listaEventosVinculables = new List<dynamic>();
                    string sqlEvt = @"SELECT ""Id_Evento"", ""Titulo"", ""Activo"" 
                              FROM ""Eventos_Catalogo"" 
                              WHERE ""Id_Evento"" != @id AND (""Activo"" = TRUE OR ""Id_Evento"" IN (SELECT ""Id_Evento_Padre"" FROM ""Eventos_ValidacionPorEvento"" WHERE ""Id_Evento"" = @id))
                              ORDER BY ""Fecha_Inicio"" DESC";
                    using (var cmdE = new NpgsqlCommand(sqlEvt, conexion))
                    {
                        cmdE.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdE.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                listaEventosVinculables.Add(new { Id = (int)r["Id_Evento"], Titulo = r["Titulo"].ToString(), Activo = (bool)r["Activo"] });
                            }
                        }
                    }
                    ViewBag.EventosVinculables = listaEventosVinculables;

                    // CARGAR ENCUESTAS PRIVADAS PARA VINCULAR (REQUISITO)
                    var listaEncuestasReq = new List<dynamic>();
                    string sqlEncReq = @"SELECT ""Id_Encuesta"", ""Titulo"" FROM ""Encuestas_Catalogo"" WHERE ""Activa"" = TRUE AND ""Es_Privada"" = TRUE ORDER BY ""Titulo"" ASC";
                    using (var cmdEncReq = new NpgsqlCommand(sqlEncReq, conexion))
                    using (var rEncReq = await cmdEncReq.ExecuteReaderAsync())
                    {
                        while (await rEncReq.ReadAsync())
                        {
                            listaEncuestasReq.Add(new { Id = (int)rEncReq["Id_Encuesta"], Titulo = rEncReq["Titulo"].ToString() });
                        }
                    }
                    ViewBag.ListaEncuestasRequisito = listaEncuestasReq;

                    // CARGAR LISTA DE GRUPOS ACTIVOS 
                    var listaGrupos = new List<dynamic>();
                    string sqlGrupos = @"SELECT ""Id_Grupo"", ""Nombre_Grupo"" FROM ""Sist_Grupos_Whatsapp"" WHERE ""Activo"" = TRUE ORDER BY ""Nombre_Grupo"" ASC";
                    using (var cmdG = new NpgsqlCommand(sqlGrupos, conexion))
                    using (var r = await cmdG.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            listaGrupos.Add(new { Id = (int)r["Id_Grupo"], Nombre = r["Nombre_Grupo"].ToString() });
                        }
                    }
                    ViewBag.ListaGrupos = listaGrupos;

                    // Lista de Usuarios
                    string sqlUsers = @"SELECT ""Id_Usuario"", ""NombreCompleto"", ""Email_Verificado"" FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE ORDER BY ""NombreCompleto"" ASC";
                    using (var cmdU = new NpgsqlCommand(sqlUsers, conexion))
                    {
                        using (var r = await cmdU.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                todosUsuarios.Add(new
                                {
                                    Id = (int)r["Id_Usuario"],
                                    Nombre = r["NombreCompleto"].ToString(),
                                    Verificado = r["Email_Verificado"] != DBNull.Value && (bool)r["Email_Verificado"]
                                });
                            }
                        }
                    }
                    ViewBag.TodosUsuarios = todosUsuarios;

                    if (id > 0)
                    {
                        // Cargar datos de Vinculación actuales
                        string sqlVinc = @"SELECT ""Id_Evento_Padre"", ""Id_Subtipo_Padre"" FROM ""Eventos_ValidacionPorEvento"" WHERE ""Id_Evento"" = @id";
                        int idEventoPadre = 0;
                        var subtiposPadre = new List<int>();
                        using (var cmdV = new NpgsqlCommand(sqlVinc, conexion))
                        {
                            cmdV.Parameters.AddWithValue("@id", id);
                            using (var rV = await cmdV.ExecuteReaderAsync())
                            {
                                while (await rV.ReadAsync())
                                {
                                    idEventoPadre = (int)rV["Id_Evento_Padre"];
                                    if (rV["Id_Subtipo_Padre"] != DBNull.Value) subtiposPadre.Add((int)rV["Id_Subtipo_Padre"]);
                                }
                            }
                        }
                        ViewBag.Id_Evento_Vinculado = idEventoPadre;
                        ViewBag.Subtipos_Vinculados_Acceso = string.Join(",", subtiposPadre);

                        // Validar Pagos
                        string sqlCheckPagos = @"SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Estatus_Pago"" IN ('paid', 'review', 'waiting_proof')";
                        using (var cmdCheck = new NpgsqlCommand(sqlCheckPagos, conexion))
                        {
                            cmdCheck.Parameters.AddWithValue("@id", id);
                            long totalPagosReales = (long)await cmdCheck.ExecuteScalarAsync();
                            ViewBag.TienePagos = totalPagosReales > 0;
                        }

                        // Cargar Evento (Incluye Es_Privado)
                        string sql = @"SELECT * FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    ViewBag.Titulo = r["Titulo"].ToString();
                                    ViewBag.Descripcion = r["Descripcion"].ToString();
                                    ViewBag.Fecha_Inicio = (DateTime)r["Fecha_Inicio"];
                                    ViewBag.Fecha_Fin = r["Fecha_Fin"] != DBNull.Value ? (DateTime)r["Fecha_Fin"] : ViewBag.Fecha_Inicio;
                                    ViewBag.Costo_Entrada = (decimal)r["Costo_Entrada"];
                                    ViewBag.Requiere_Registro = (bool)r["Requiere_Registro"];
                                    ViewBag.Tipo_Registro = (int)r["Tipo_Registro"];
                                    ViewBag.Cupo_Maximo = (int)r["Cupo_Maximo"];
                                    ViewBag.Imagen_Url = r["Imagen_Url"]?.ToString();
                                    ViewBag.Es_Destacado = (bool)r["Es_Destacado"];
                                    ViewBag.Activo = (bool)r["Activo"];
                                    ViewBag.Permitir_Pago = (bool)r["Permitir_Pago"];
                                    ViewBag.Permitir_Pagos_Parciales = r["Permitir_Pagos_Parciales"] != DBNull.Value && (bool)r["Permitir_Pagos_Parciales"];
                                    ViewBag.ClaveCuenta = r["Clave_Cuenta_Bancaria"]?.ToString() ?? "DEFAULT";
                                    ViewBag.Es_Privado = r["Es_Privado"] != DBNull.Value && (bool)r["Es_Privado"];
                                    ViewBag.Id_Grupo_Usuarios = r["Id_Grupo_Usuarios"] != DBNull.Value ? (int)r["Id_Grupo_Usuarios"] : 0;
                                    ViewBag.Permitir_Transferencia = r["Permitir_Transferencia"] != DBNull.Value && (bool)r["Permitir_Transferencia"];
                                    ViewBag.Permitir_Pago_Tarjeta = r["Permitir_Pago_Tarjeta"] != DBNull.Value ? (bool)r["Permitir_Pago_Tarjeta"] : true;
                                    ViewBag.Cobrar_Comision_Extra = r["Cobrar_Comision_Extra"] != DBNull.Value ? (bool)r["Cobrar_Comision_Extra"] : true;
                                    ViewBag.Bloqueado = r["Bloqueado"] != DBNull.Value && (bool)r["Bloqueado"];
                                    ViewBag.Id_Encuesta_Requisito = r["Id_Encuesta_Requisito"] != DBNull.Value ? (int)r["Id_Encuesta_Requisito"] : 0;

                                    ViewBag.Usa_Cuenta_Personalizada = r["Usa_Cuenta_Personalizada"] != DBNull.Value && (bool)r["Usa_Cuenta_Personalizada"];
                                    ViewBag.Banco_Custom = r["Banco_Custom"] != DBNull.Value ? r["Banco_Custom"].ToString() : "";
                                    ViewBag.Titular_Custom = r["Titular_Custom"] != DBNull.Value ? r["Titular_Custom"].ToString() : "";
                                    ViewBag.Clabe_Custom = r["Clabe_Custom"] != DBNull.Value ? r["Clabe_Custom"].ToString() : "";
                                    ViewBag.Cuenta_Custom = r["Cuenta_Custom"] != DBNull.Value ? r["Cuenta_Custom"].ToString() : "";
                                }
                            }
                        }

                        // Cargar Calendario
                        string sqlCal = @"SELECT * FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @id AND ""Id_Subtipo"" IS NULL AND ""Numero_Pago"" > 0 ORDER BY ""Numero_Pago"" ASC";
                        using (var cmdCal = new NpgsqlCommand(sqlCal, conexion))
                        {
                            cmdCal.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdCal.ExecuteReaderAsync())
                            {
                                var listaPagos = new List<CalendarioPagoItem>();
                                while (await r.ReadAsync()) listaPagos.Add(new CalendarioPagoItem { Id_Calendario_Pago = (int)r["Id_Calendario"], Numero_Pago = (int)r["Numero_Pago"], Fecha_Limite = (DateTime)r["Fecha_Limite"], Monto = (decimal)r["Monto_Base"] });
                                ViewBag.CalendarioPagos = listaPagos;
                            }
                        }

                        // Cargar Preguntas
                        string sqlP = @"SELECT * FROM ""Eventos_Preguntas"" WHERE ""Id_Evento"" = @id ORDER BY ""Id_Pregunta"" ASC";
                        using (var cmdP = new NpgsqlCommand(sqlP, conexion))
                        {
                            cmdP.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdP.ExecuteReaderAsync())
                            {
                                var listaP = new List<PreguntaConfigitem>();
                                while (await r.ReadAsync()) listaP.Add(new PreguntaConfigitem { Id_Pregunta = (int)r["Id_Pregunta"], Texto = r["Texto_Pregunta"].ToString(), Tipo = (TipoPregunta)(int)r["Tipo_Pregunta"], Opciones = r["Opciones"]?.ToString() });
                                ViewBag.Preguntas = listaP;
                            }
                        }

                        // Cargar Subtipos (CON CONTEO DE OCUPADOS)
                        string sqlSub = @"SELECT s.*, 
                                  (SELECT COUNT(*) FROM ""Eventos_B_Asistentes"" a WHERE a.""Id_Subtipo"" = s.""Id_Subtipo"") as ""Ocupados""
                                  FROM ""Eventos_Subtipos"" s 
                                  WHERE s.""Id_Evento"" = @id AND s.""Activo"" = TRUE 
                                  ORDER BY s.""Costo"" ASC, s.""Nombre"" ASC";

                        var listaSub = new List<SubtipoEventoItem>();
                        using (var cmdS = new NpgsqlCommand(sqlSub, conexion))
                        {
                            cmdS.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdS.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    listaSub.Add(new SubtipoEventoItem
                                    {
                                        Id_Subtipo = (int)r["Id_Subtipo"],
                                        Nombre = r["Nombre"].ToString(),
                                        Costo = (decimal)r["Costo"],
                                        Cupo = (int)r["Cupo"],
                                        Ocupados = Convert.ToInt32(r["Ocupados"]) // <--- ESTO FALTABA
                                    });
                                }
                            }
                        }
                        ViewBag.Subtipos = listaSub;

                        // Cargar Productos Extra
                        string sqlExt = @"SELECT * FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_Evento"" = @id ORDER BY ""Id_ProductoExtra"" ASC";
                        var listaExtra = new List<ProductoExtraItem>();
                        using (var cmdE = new NpgsqlCommand(sqlExt, conexion))
                        {
                            cmdE.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdE.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    listaExtra.Add(new ProductoExtraItem
                                    {
                                        Id_ProductoExtra = (int)r["Id_ProductoExtra"],
                                        Id_Subtipo = r["Id_Subtipo"] as int?,
                                        Nombre_Producto = r["Nombre_Producto"].ToString(),
                                        Descripcion = r["Descripcion"]?.ToString(),
                                        Precio = (decimal)r["Precio"],
                                        Cantidad_Minima = (int)r["Cantidad_Minima"],
                                        Cantidad_Maxima = (int)r["Cantidad_Maxima"],
                                        Activo = (bool)r["Activo"]
                                    });
                                }
                                ViewBag.ProductosExtra = listaExtra;
                            }
                        }

                        // Cargar Galería
                        string sqlGal = @"SELECT * FROM ""Eventos_Galeria"" WHERE ""Id_Evento"" = @id ORDER BY ""Orden"" ASC";
                        var listaGal = new List<FotoGaleriaItem>();
                        using (var cmdG = new NpgsqlCommand(sqlGal, conexion))
                        {
                            cmdG.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdG.ExecuteReaderAsync()) while (await r.ReadAsync()) listaGal.Add(new FotoGaleriaItem { Id_Foto = (int)r["Id_Foto"], Url_Foto = r["Url_Foto"].ToString(), Descripcion = r["Descripcion"]?.ToString() });
                        }
                        ViewBag.Galeria = listaGal;

                        // Cargar Inscriptores
                        string sqlIns = @"SELECT i.""Id_Usuario"", u.""NombreCompleto"" FROM ""Eventos_Inscriptores"" i JOIN ""Sist_Usuarios"" u ON i.""Id_Usuario"" = u.""Id_Usuario"" WHERE i.""Id_Evento"" = @id";
                        var listaIns = new List<InscriptorItem>();
                        using (var cmdI = new NpgsqlCommand(sqlIns, conexion))
                        {
                            cmdI.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdI.ExecuteReaderAsync()) while (await r.ReadAsync()) listaIns.Add(new InscriptorItem { Id_Usuario = (int)r["Id_Usuario"], NombreUsuario = r["NombreCompleto"].ToString() });
                        }
                        ViewBag.Inscriptores = listaIns;

                        // Calcular Intocables (Historial)
                        string sqlBloqueados = @"SELECT DISTINCT ""Id_Usuario"" FROM ""Eventos_A_Registros"" WHERE ""Id_Evento"" = @id";
                        using (var cmdB = new NpgsqlCommand(sqlBloqueados, conexion))
                        {
                            cmdB.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdB.ExecuteReaderAsync()) while (await r.ReadAsync()) inscriptoresConHistorial.Add((int)r["Id_Usuario"]);
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error al cargar", ex.Message, TipoMensaje.Error); }

            ViewBag.InscriptoresConHistorial = inscriptoresConHistorial;
            return View();
        }

        /// <summary>
        /// Guarda la información de un evento, ya sea nuevo o modificado.
        /// </summary>
        /// <param name="modelo">Es el modelo con los datos del evento a guardar.</param>
        /// <returns>Es la redirección a la vista de índice de eventos.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EditorEventoViewModel modelo, IFormFile fotoPortadaNueva, List<IFormFile> nuevasFotosGaleria, List<string> imagenesAEliminar, List<string> descripcionesNuevasFotos, int Id_Evento_Vinculado = 0, string Subtipos_Vinculados_Acceso = "", bool Usa_Cuenta_Personalizada = false, string Banco_Custom = "", string Titular_Custom = "", string Clabe_Custom = "", string Cuenta_Custom = "")
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Sin Permiso", "No tienes permiso para realizar esta acción.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            if (Usa_Cuenta_Personalizada)
            {
                modelo.Permitir_Pago_Tarjeta = false;
                modelo.Permitir_Transferencia = true;
            }

            string sIdEventoEncriptado = "";

            // Si los pagos están activos, debe haber al menos un método seleccionado
            if (modelo.Permitir_Pago && !modelo.Permitir_Pago_Tarjeta && !modelo.Permitir_Transferencia)
            {
                sIdEventoEncriptado = Funciones.EncriptarId(modelo.Id_Evento);
                MostrarMensaje("Atención", "Debes habilitar al menos un método de pago (Tarjeta o Transferencia).", TipoMensaje.Alerta);
                return RedirectToAction("Editor", new { sid = sIdEventoEncriptado });
            }

            // =========================================================================================
            // 1. VALIDACIÓN PREVIA DE ARCHIVOS: Verificamos peso y formato de imágenes antes de abrir 
            // conexiones a la base de datos para ahorrar recursos y mejorar el tiempo de respuesta.
            // =========================================================================================
            string[] extensionesValidas = { ".jpg", ".jpeg", ".png", ".webp" };
            var todasLasFotos = new List<IFormFile>();
            if (fotoPortadaNueva != null) todasLasFotos.Add(fotoPortadaNueva);
            if (nuevasFotosGaleria != null) todasLasFotos.AddRange(nuevasFotosGaleria);

            foreach (var f in todasLasFotos)
            {
                if (f.Length == 0 || f.Length > 10 * 1024 * 1024)
                {
                    MostrarMensaje("Error", $"La imagen {f.FileName} supera los 10MB o está dañada.", TipoMensaje.Error);
                    return RedirectToAction("Editor", new { sid = sIdEventoEncriptado });
                }
                string ext = Path.GetExtension(f.FileName).ToLower();
                if (!extensionesValidas.Contains(ext))
                {
                    MostrarMensaje("Error", $"Formato no válido en {f.FileName}.", TipoMensaje.Error);
                    return RedirectToAction("Editor", new { sid = sIdEventoEncriptado });
                }
            }

            try
            {
                sIdEventoEncriptado = Funciones.EncriptarId(modelo.Id_Evento);

                // =========================================================================================
                // 2. VALIDACIÓN MATEMÁTICA: Si el evento permite pagos en plazos (parcialidades), 
                // es estrictamente necesario que la suma de los montos de cada plazo coincida exactamente 
                // con el Costo Base General configurado para el evento.
                // =========================================================================================
                if (modelo.Permitir_Pagos_Parciales && modelo.CalendarioPagos != null)
                {
                    // Sumamos solo los pagos que están visualmente activos en el frontend (ignoramos los que el usuario "eliminó")
                    // Estos totales son para el calendario base de ahi si todo cuadra se tomará como ejemplo para calcular los demás calendarios
                    decimal sumaPlazos = modelo.CalendarioPagos
                                         .Where(p => !p.Eliminado)
                                         .Sum(p => p.Monto);

                    // Si hay descuadre, bloqueamos el guardado y regresamos al editor
                    if (sumaPlazos != modelo.Costo_Entrada)
                    {
                        MostrarMensaje("Error de Cálculo",
                            $"La suma de los plazos (${sumaPlazos:N2}) no coincide con el Costo General (${modelo.Costo_Entrada:N2}). Ajusta los montos.",
                            TipoMensaje.Error);

                        return RedirectToAction("Editor", new { sid = sIdEventoEncriptado });
                    }
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // =========================================================================================
                    // 3. MAPEO DE MODALIDADES EN USO: Buscamos qué modalidades (Subtipos) ya tienen usuarios.
                    // Regla: Si una modalidad ya tiene gente inscrita, NO se le puede cambiar 
                    // el costo (para evitar descuadres en sus deudas) y NO se puede eliminar de la BD.
                    // =========================================================================================
                    var ListaSubtiposConRegistros = new List<int>();
                    if (modelo.Id_Evento > 0)
                    {
                        string sql1 = "SELECT \"Id_Subtipo\" FROM \"Eventos_B_Asistentes\" AS b" +
                            " LEFT JOIN \"Eventos_A_Registros\" AS a ON a.\"Id_Registro\" = B.\"Id_Registro\"" +
                            " WHERE \"Id_Evento\" = @id AND NOT \"Id_Subtipo\" IS NULL";
                        using (var cmd = new NpgsqlCommand(sql1, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", modelo.Id_Evento);
                            using (var dr = await cmd.ExecuteReaderAsync())
                            {
                                while (await dr.ReadAsync())
                                {
                                    ListaSubtiposConRegistros.Add((int)dr[0]);
                                }
                            }
                        }
                    }

                    // =========================================================================================
                    // 4. CANDADO FINANCIERO DE SEGURIDAD (CRÍTICO): 
                    // Verificamos si en este evento ya existen pagos procesados, validados o en revisión.
                    // Si es así (true), activamos un escudo que impedirá borrar calendarios existentes o 
                    // modificar el costo general para proteger la contabilidad del sistema.
                    // =========================================================================================
                    bool BloquearBorradoCalendarioPagos = false;
                    if (modelo.Id_Evento > 0)
                    {
                        string sqlCheck = @"SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" 
                    WHERE ""Id_Evento"" = @id 
                    AND ""Estatus_Pago"" IN ('paid', 'review', 'waiting_proof')";

                        using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", modelo.Id_Evento);
                            long pagosReales = (long)await cmd.ExecuteScalarAsync();
                            BloquearBorradoCalendarioPagos = pagosReales > 0;
                        }
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int idEvento = modelo.Id_Evento;

                            if (modelo.Es_Destacado)
                            {
                                // Regla: Solo puede haber un evento destacado a la vez. 
                                // Apagamos la bandera 'Es_Destacado' de cualquier otro evento en la BD.
                                string sqlReset = @"UPDATE ""Eventos_Catalogo"" SET ""Es_Destacado"" = FALSE WHERE ""Id_Evento"" != @id";
                                using (var cmdReset = new NpgsqlCommand(sqlReset, conexion, trans))
                                {
                                    cmdReset.Parameters.AddWithValue("@id", idEvento);
                                    await cmdReset.ExecuteNonQueryAsync();
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO A: GUARDAR DATOS PRINCIPALES DEL EVENTO (CATÁLOGO)
                            // ---------------------------------------------------------------------------------
                            if (idEvento == 0)
                            {
                                string sql2 = @"INSERT INTO ""Eventos_Catalogo"" 
                                   (""Titulo"", ""Descripcion"", ""Fecha_Inicio"", ""Fecha_Fin"", ""Costo_Entrada"", 
                                    ""Requiere_Registro"", ""Tipo_Registro"", ""Cupo_Maximo"", ""Imagen_Url"", 
                                    ""Es_Destacado"", ""Activo"", ""Permitir_Pago"", ""Permitir_Pagos_Parciales"", 
                                    ""Clave_Cuenta_Bancaria"", ""Es_Privado"", ""Permitir_Transferencia"", ""Permitir_Pago_Tarjeta"", ""Cobrar_Comision_Extra"", ""Id_Grupo_Usuarios"", ""Bloqueado"",
                                    ""Usa_Cuenta_Personalizada"", ""Banco_Custom"", ""Titular_Custom"", ""Clabe_Custom"", ""Cuenta_Custom"", ""Id_Encuesta_Requisito"") 
                                   VALUES 
                                   (@tit, @desc, @ini, @fin, @costo, 
                                    @req, @tipo, @cupo, @img, 
                                    @dest, @act, @pago, @parcial, 
                                    @claveBanco, @priv, @permTransf, @permTarjeta, @cobrarComisionExtra, @idGrupo, @bloq,
                                    @usaCust, @bancoCust, @titularCust, @clabeCust, @cuentaCust, @encReq) 
                                   RETURNING ""Id_Evento""";

                                using (var cmd = new NpgsqlCommand(sql2, conexion, trans))
                                {
                                    SetParams(cmd, modelo);
                                    cmd.Parameters.AddWithValue("@parcial", modelo.Permitir_Pagos_Parciales);
                                    cmd.Parameters.AddWithValue("@priv", modelo.Es_Privado);
                                    cmd.Parameters.AddWithValue("@permTransf", modelo.Permitir_Transferencia);
                                    cmd.Parameters.AddWithValue("@permTarjeta", modelo.Permitir_Pago_Tarjeta);
                                    cmd.Parameters.AddWithValue("@cobrarComisionExtra", modelo.Cobrar_Comision_Extra);
                                    cmd.Parameters.AddWithValue("@bloq", modelo.Bloqueado);

                                    cmd.Parameters.AddWithValue("@usaCust", Usa_Cuenta_Personalizada);
                                    cmd.Parameters.AddWithValue("@bancoCust", Banco_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titularCust", Titular_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabeCust", Clabe_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@cuentaCust", Cuenta_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@encReq", modelo.Id_Encuesta_Requisito > 0 ? (object)modelo.Id_Encuesta_Requisito.Value : DBNull.Value);

                                    idEvento = (int)await cmd.ExecuteScalarAsync();
                                }

                                if (Usa_Cuenta_Personalizada)
                                {
                                    string sqlUpdateClave = @"UPDATE ""Eventos_Catalogo"" SET ""Clave_Cuenta_Bancaria"" = @clave WHERE ""Id_Evento"" = @id";
                                    using (var cmdClave = new NpgsqlCommand(sqlUpdateClave, conexion, trans))
                                    {
                                        cmdClave.Parameters.AddWithValue("@clave", idEvento.ToString());
                                        cmdClave.Parameters.AddWithValue("@id", idEvento);
                                        await cmdClave.ExecuteNonQueryAsync();
                                    }
                                }
                            }
                            else
                            {
                                // Aplicación del Candado Financiero: 
                                // Si hay pagos (bloquearCambiosFinancieros = true), excluimos el Costo_Entrada 
                                // y Permitir_Pagos_Parciales del UPDATE para que el Admin no pueda corromper la deuda de los ya inscritos.
                                string sqlCosto = BloquearBorradoCalendarioPagos ? "" : @", ""Costo_Entrada""=@costo, ""Permitir_Pagos_Parciales""=@parcial";

                                string sql = $@"UPDATE ""Eventos_Catalogo"" 
                                SET ""Titulo""=@tit, ""Descripcion""=@desc, ""Fecha_Inicio""=@ini, ""Fecha_Fin""=@fin, 
                                    ""Requiere_Registro""=@req, ""Tipo_Registro""=@tipo, ""Cupo_Maximo""=@cupo, 
                                    ""Imagen_Url""=@img, ""Es_Destacado""=@dest, ""Activo""=@act, 
                                    ""Permitir_Pago""=@pago, ""Clave_Cuenta_Bancaria""=@claveBanco, ""Es_Privado""=@priv, 
                                    ""Permitir_Transferencia"" = @permTransf, ""Permitir_Pago_Tarjeta"" = @permTarjeta, ""Cobrar_Comision_Extra"" = @cobrarComisionExtra, ""Id_Grupo_Usuarios"" = @idGrupo,
                                    ""Bloqueado"" = @bloq,
                                    ""Usa_Cuenta_Personalizada""=@usaCust, ""Banco_Custom""=@bancoCust, ""Titular_Custom""=@titularCust, ""Clabe_Custom""=@clabeCust, ""Cuenta_Custom""=@cuentaCust,
                                    ""Id_Encuesta_Requisito""=@encReq
                                    {sqlCosto} 
                                WHERE ""Id_Evento""=@id";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    SetParams(cmd, modelo);
                                    if (!BloquearBorradoCalendarioPagos)
                                    {
                                        cmd.Parameters.AddWithValue("@parcial", modelo.Permitir_Pagos_Parciales);
                                    }
                                    cmd.Parameters.AddWithValue("@priv", modelo.Es_Privado);
                                    cmd.Parameters.AddWithValue("@id", idEvento);
                                    cmd.Parameters.AddWithValue("@permTransf", modelo.Permitir_Transferencia);
                                    cmd.Parameters.AddWithValue("@permTarjeta", modelo.Permitir_Pago_Tarjeta);
                                    cmd.Parameters.AddWithValue("@cobrarComisionExtra", modelo.Cobrar_Comision_Extra);
                                    cmd.Parameters.AddWithValue("@bloq", modelo.Bloqueado);

                                    cmd.Parameters.AddWithValue("@usaCust", Usa_Cuenta_Personalizada);
                                    cmd.Parameters.AddWithValue("@bancoCust", Banco_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titularCust", Titular_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabeCust", Clabe_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@cuentaCust", Cuenta_Custom ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@encReq", modelo.Id_Encuesta_Requisito > 0 ? (object)modelo.Id_Encuesta_Requisito.Value : DBNull.Value);

                                    if (Usa_Cuenta_Personalizada)
                                    {
                                        cmd.Parameters["@claveBanco"].Value = idEvento.ToString();
                                    }

                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO A.1: VINCULACIÓN DEL EVENTO PARA VALIDACIÓN ADICIONAL
                            // ---------------------------------------------------------------------------------
                            if (Id_Evento_Vinculado > 0)
                            {
                                var subtiposNuevos = new List<int>();
                                if (!string.IsNullOrEmpty(Subtipos_Vinculados_Acceso))
                                {
                                    subtiposNuevos = Subtipos_Vinculados_Acceso.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                                }

                                // Borramos la regla anterior e insertamos la nueva. La función central de acceso respetará a los ya inscritos.
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_ValidacionPorEvento\" WHERE \"Id_Evento\" = {idEvento}", conexion, trans).ExecuteNonQueryAsync();

                                if (subtiposNuevos.Any())
                                {
                                    foreach (var sub in subtiposNuevos)
                                    {
                                        string sqlV = @"INSERT INTO ""Eventos_ValidacionPorEvento"" (""Id_Evento"", ""Id_Evento_Padre"", ""Id_Subtipo_Padre"") VALUES (@id, @padre, @sub)";
                                        using (var cmdV = new NpgsqlCommand(sqlV, conexion, trans))
                                        {
                                            cmdV.Parameters.AddWithValue("@id", idEvento);
                                            cmdV.Parameters.AddWithValue("@padre", Id_Evento_Vinculado);
                                            cmdV.Parameters.AddWithValue("@sub", sub);
                                            await cmdV.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                                else
                                {
                                    string sqlV = @"INSERT INTO ""Eventos_ValidacionPorEvento"" (""Id_Evento"", ""Id_Evento_Padre"", ""Id_Subtipo_Padre"") VALUES (@id, @padre, NULL)";
                                    using (var cmdV = new NpgsqlCommand(sqlV, conexion, trans))
                                    {
                                        cmdV.Parameters.AddWithValue("@id", idEvento);
                                        cmdV.Parameters.AddWithValue("@padre", Id_Evento_Vinculado);
                                        await cmdV.ExecuteNonQueryAsync();
                                    }
                                }
                            }
                            else
                            {
                                // Si no seleccionó evento vinculado, simplemente eliminamos cualquier vinculación previa
                                if (modelo.Id_Evento > 0)
                                {
                                    await new NpgsqlCommand($"DELETE FROM \"Eventos_ValidacionPorEvento\" WHERE \"Id_Evento\" = {idEvento}", conexion, trans).ExecuteNonQueryAsync();
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO B: GESTIÓN DE MODALIDADES (SUBTIPOS)
                            // Comparamos lo que envió la vista vs lo que existe en BD para determinar Borrados, Updates e Inserts
                            // ---------------------------------------------------------------------------------

                            // B.1 Obtener los IDs de las modalidades que actualmente existen en la base de datos.
                            var idsEnBD = new List<int>();
                            using (var cmdIds = new NpgsqlCommand(@"SELECT ""Id_Subtipo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento""=@id", conexion, trans))
                            {
                                cmdIds.Parameters.AddWithValue("@id", idEvento);
                                using (var r = await cmdIds.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync())
                                    {
                                        idsEnBD.Add((int)r[0]);
                                    }
                                }
                            }

                            // B.2 Recolectar IDs de modalidades que vienen del formulario (ignorando las que el admin borró visualmente).
                            var idsEnFormulario = new List<int>();
                            if (modelo.Subtipos != null)
                            {
                                foreach (var s in modelo.Subtipos)
                                {
                                    // Si s.Activo es false, significa que el usuario hizo clic en "Borrar" en el frontend.
                                    // Al no agregarlo a 'idsEnFormulario', el siguiente paso lo detectará como candidato a borrar.
                                    if (s.Id_Subtipo > 0 && s.Activo)
                                    {
                                        idsEnFormulario.Add(s.Id_Subtipo);
                                    }
                                }
                            }

                            // B.3 Calcular discrepancia: IDs que están en la BD pero ya no están vigentes en el Formulario.
                            var aBorrar = new List<int>();
                            aBorrar = idsEnBD.Except(idsEnFormulario).ToList();

                            // B.4 Ejecutar Borrados (Hard Delete vs Soft Delete basado en la regla de uso)
                            foreach (var idSub in aBorrar)
                            {
                                bool bSePuedeBorrar = false;
                                // Evaluamos si la modalidad está completamente virgen (no tiene gente inscrita)
                                if (!ListaSubtiposConRegistros.Contains(idSub))
                                {
                                    bSePuedeBorrar = true;
                                }

                                if (bSePuedeBorrar)
                                {
                                    // Al no tener gente, podemos borrar su calendario particular ("calendario espejo") y el registro por completo (Hard Delete).
                                    string sqlCleanCal = @"DELETE FROM ""Eventos_Calendario"" WHERE ""Id_Subtipo"" = @id";
                                    using (var cmdCl = new NpgsqlCommand(sqlCleanCal, conexion, trans))
                                    {
                                        cmdCl.Parameters.AddWithValue("@id", idSub);
                                        await cmdCl.ExecuteNonQueryAsync();
                                    }

                                    await new NpgsqlCommand($"DELETE FROM \"Eventos_Subtipos\" WHERE \"Id_Subtipo\"={idSub}", conexion, trans).ExecuteNonQueryAsync();
                                }
                                else
                                {
                                    // Como ya tiene gente, un borrado real rompería las Foreign Keys y dejaría a los usuarios huérfanos.
                                    // Solución: Solo la ocultamos para futuros registros (Soft Delete).
                                    await new NpgsqlCommand($"UPDATE \"Eventos_Subtipos\" SET \"Activo\"=FALSE WHERE \"Id_Subtipo\"={idSub}", conexion, trans).ExecuteNonQueryAsync();
                                }
                            }

                            // B.5 Insertar y Actualizar las modalidades vigentes
                            if (modelo.Subtipos != null)
                            {
                                foreach (var s in modelo.Subtipos)
                                {
                                    if (!s.Activo) continue; // Si se marcó para borrar en la vista, ya lo tratamos arriba.

                                    if (s.Id_Subtipo == 0) // Es una modalidad completamente nueva
                                    {
                                        string sqlInsSub = @"INSERT INTO ""Eventos_Subtipos"" 
                                     (""Id_Evento"", ""Nombre"", ""Costo"", ""Cupo"", ""Activo"") 
                                     VALUES (@ev, @nom, @costo, @cupo, TRUE)";

                                        using (var cmdS = new NpgsqlCommand(sqlInsSub, conexion, trans))
                                        {
                                            cmdS.Parameters.AddWithValue("@ev", idEvento);
                                            cmdS.Parameters.AddWithValue("@nom", s.Nombre);
                                            cmdS.Parameters.AddWithValue("@costo", s.Costo);
                                            cmdS.Parameters.AddWithValue("@cupo", s.Cupo);
                                            await cmdS.ExecuteNonQueryAsync();
                                        }
                                    }
                                    else // Actualizar modalidad existente
                                    {
                                        bool bSePuedeEditar = false;
                                        // Volvemos a aplicar la regla de seguridad: ¿Tiene gente inscrita?
                                        if (!ListaSubtiposConRegistros.Contains(s.Id_Subtipo))
                                        {
                                            bSePuedeEditar = true;
                                        }

                                        string sqlUpdSub;
                                        if (bSePuedeEditar)
                                        {
                                            // Modalidad vacía: Permite editar su Nombre, Cupo y su COSTO.
                                            sqlUpdSub = @"UPDATE ""Eventos_Subtipos"" SET ""Nombre""=@nom, ""Costo""=@costo, ""Cupo""=@cupo, ""Activo""=TRUE WHERE ""Id_Subtipo""=@id";
                                        }
                                        else
                                        {
                                            // Modalidad en uso (Candado parcial): Excluimos el parámetro Costo del UPDATE. Solo se puede editar Nombre y Cupo.
                                            sqlUpdSub = @"UPDATE ""Eventos_Subtipos"" SET ""Nombre""=@nom, ""Cupo""=@cupo, ""Activo""=TRUE WHERE ""Id_Subtipo""=@id";
                                        }

                                        using (var cmdS = new NpgsqlCommand(sqlUpdSub, conexion, trans))
                                        {
                                            cmdS.Parameters.AddWithValue("@nom", s.Nombre);

                                            if (bSePuedeEditar) // Inyectar costo solo si la consulta SQL armada lo pide
                                            {
                                                cmdS.Parameters.AddWithValue("@costo", s.Costo);
                                            }

                                            cmdS.Parameters.AddWithValue("@cupo", s.Cupo);
                                            cmdS.Parameters.AddWithValue("@id", s.Id_Subtipo);
                                            await cmdS.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // B.6 Insertar y Actualizar Productos Extra
                            if (modelo.ProductosExtra != null)
                            {
                                foreach (var p in modelo.ProductosExtra)
                                {
                                    if (!p.Activo && p.Id_ProductoExtra > 0)
                                    {
                                        // Desactivar
                                        await new NpgsqlCommand($"UPDATE \"Eventos_Catalogo_ProductosExtra\" SET \"Activo\"=FALSE WHERE \"Id_ProductoExtra\"={p.Id_ProductoExtra}", conexion, trans).ExecuteNonQueryAsync();
                                        continue;
                                    }
                                    if (!p.Activo) continue;

                                    if (p.Id_ProductoExtra == 0) // Nuevo
                                    {
                                        string sqlInsP = @"INSERT INTO ""Eventos_Catalogo_ProductosExtra"" 
                                     (""Id_Evento"", ""Id_Subtipo"", ""Nombre_Producto"", ""Descripcion"", ""Precio"", ""Cantidad_Minima"", ""Cantidad_Maxima"", ""Activo"") 
                                     VALUES (@ev, @sub, @nom, @desc, @pre, @cmin, @cmax, TRUE) RETURNING ""Id_ProductoExtra""";

                                        using (var cmdP = new NpgsqlCommand(sqlInsP, conexion, trans))
                                        {
                                            cmdP.Parameters.AddWithValue("@ev", idEvento);
                                            cmdP.Parameters.Add(new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)p.Id_Subtipo ?? DBNull.Value });
                                            cmdP.Parameters.AddWithValue("@nom", p.Nombre_Producto);
                                            cmdP.Parameters.Add(new NpgsqlParameter("@desc", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = string.IsNullOrEmpty(p.Descripcion) ? DBNull.Value : (object)p.Descripcion });
                                            cmdP.Parameters.AddWithValue("@pre", p.Precio);
                                            cmdP.Parameters.AddWithValue("@cmin", p.Cantidad_Minima);
                                            cmdP.Parameters.AddWithValue("@cmax", p.Cantidad_Maxima);
                                            p.Id_ProductoExtra = (int)await cmdP.ExecuteScalarAsync();
                                        }
                                    }
                                    else // Actualizar
                                    {
                                        string sqlUpdP = @"UPDATE ""Eventos_Catalogo_ProductosExtra"" 
                                     SET ""Id_Subtipo""=@sub, ""Nombre_Producto""=@nom, ""Descripcion""=@desc, ""Precio""=@pre, ""Cantidad_Minima""=@cmin, ""Cantidad_Maxima""=@cmax, ""Activo""=TRUE 
                                     WHERE ""Id_ProductoExtra""=@id";

                                        using (var cmdP = new NpgsqlCommand(sqlUpdP, conexion, trans))
                                        {
                                            cmdP.Parameters.Add(new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)p.Id_Subtipo ?? DBNull.Value });
                                            cmdP.Parameters.AddWithValue("@nom", p.Nombre_Producto);
                                            cmdP.Parameters.Add(new NpgsqlParameter("@desc", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = string.IsNullOrEmpty(p.Descripcion) ? DBNull.Value : (object)p.Descripcion });
                                            cmdP.Parameters.AddWithValue("@pre", p.Precio);
                                            cmdP.Parameters.AddWithValue("@cmin", p.Cantidad_Minima);
                                            cmdP.Parameters.AddWithValue("@cmax", p.Cantidad_Maxima);
                                            cmdP.Parameters.AddWithValue("@id", p.Id_ProductoExtra);
                                            await cmdP.ExecuteNonQueryAsync();
                                        }
                                    }

                                    // Sincronizar Calendario para el Producto Extra
                                    string sqlCheckCalExt = @"SELECT COUNT(*) FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago""=@np";
                                    using (var cmdCheck = new NpgsqlCommand(sqlCheckCalExt, conexion, trans))
                                    {
                                        cmdCheck.Parameters.AddWithValue("@ev", idEvento);
                                        cmdCheck.Parameters.AddWithValue("@np", -(10000 + p.Id_ProductoExtra));
                                        long count = (long)await cmdCheck.ExecuteScalarAsync();
                                        
                                        if (count == 0)
                                        {
                                            string sqlInsCalExt = @"INSERT INTO ""Eventos_Calendario"" (""Id_Evento"", ""Id_Subtipo"", ""Numero_Pago"", ""Nombre_Concepto"", ""Fecha_Limite"", ""Monto_Base"") 
                                                                    VALUES (@ev, NULL, @np, @nom, @fl, @pre)";
                                            using (var cmdInsC = new NpgsqlCommand(sqlInsCalExt, conexion, trans))
                                            {
                                                cmdInsC.Parameters.AddWithValue("@ev", idEvento);
                                                cmdInsC.Parameters.AddWithValue("@np", -(10000 + p.Id_ProductoExtra));
                                                cmdInsC.Parameters.AddWithValue("@nom", p.Nombre_Producto);
                                                cmdInsC.Parameters.AddWithValue("@fl", modelo.Fecha_Inicio);
                                                cmdInsC.Parameters.AddWithValue("@pre", p.Precio);
                                                await cmdInsC.ExecuteNonQueryAsync();
                                            }
                                        }
                                        else
                                        {
                                            string sqlUpdCalExt = @"UPDATE ""Eventos_Calendario"" SET ""Nombre_Concepto""=@nom, ""Monto_Base""=@pre WHERE ""Id_Evento""=@ev AND ""Numero_Pago""=@np";
                                            using (var cmdUpdC = new NpgsqlCommand(sqlUpdCalExt, conexion, trans))
                                            {
                                                cmdUpdC.Parameters.AddWithValue("@nom", p.Nombre_Producto);
                                                cmdUpdC.Parameters.AddWithValue("@pre", p.Precio);
                                                cmdUpdC.Parameters.AddWithValue("@ev", idEvento);
                                                cmdUpdC.Parameters.AddWithValue("@np", -(10000 + p.Id_ProductoExtra));
                                                await cmdUpdC.ExecuteNonQueryAsync();
                                            }
                                        }
                                    }
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO C: CALENDARIO INTELIGENTE Y GENERACIÓN DE DEUDAS (NÚCLEO FINANCIERO)
                            // Aquí se crean los plazos de cobro. Se genera una "Plantilla Maestra" general 
                            // y luego se clona para cada modalidad ajustando los precios proporcionalmente ("Espejos").
                            // ---------------------------------------------------------------------------------

                            if (!BloquearBorradoCalendarioPagos)
                            {
                                // ESCENARIO A: EL EVENTO ESTÁ LIMPIO FINANCIERAMENTE.
                                // Como nadie ha pagado nada, podemos darnos el lujo de borrar todos los calendarios 
                                // y regenerarlos desde cero según lo que capturó el administrador en el formulario.

                                // C.1 Limpieza en Cascada: Pagos Aplicados -> Cuentas por Cobrar (Deudas) -> Calendario (PROTEGIENDO PRODUCTOS EXTRA Numero_Pago <= -10000)
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_E_Pagos_Aplicados\" WHERE \"Id_Cuenta\" IN (SELECT c.\"Id_Cuenta\" FROM \"Eventos_C_Cuentas_Cobrar\" c JOIN \"Eventos_Calendario\" cal ON c.\"Id_Calendario\" = cal.\"Id_Calendario\" WHERE cal.\"Id_Evento\" = {idEvento} AND (cal.\"Numero_Pago\" > 0 OR cal.\"Numero_Pago\" = -1))", conexion, trans).ExecuteNonQueryAsync();
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Calendario\" IN (SELECT \"Id_Calendario\" FROM \"Eventos_Calendario\" WHERE \"Id_Evento\" = {idEvento} AND (\"Numero_Pago\" > 0 OR \"Numero_Pago\" = -1))", conexion, trans).ExecuteNonQueryAsync();
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_Calendario\" WHERE \"Id_Evento\" = {idEvento} AND (\"Numero_Pago\" > 0 OR \"Numero_Pago\" = -1)", conexion, trans).ExecuteNonQueryAsync();

                                // C.2 Recuperar la "Plantilla" (Los plazos que configuró el Admin en la vista)
                                var calendarioTemplate = new List<CalendarioPagoItem>();
                                if (modelo.Permitir_Pagos_Parciales && modelo.CalendarioPagos != null && modelo.CalendarioPagos.Any(x => !x.Eliminado))
                                {
                                    calendarioTemplate = modelo.CalendarioPagos
                                        .Where(p => !p.Eliminado)
                                        .OrderBy(x => x.Fecha_Limite)
                                        .ToList();
                                }
                                else
                                {
                                    // Si el admin no configuró plazos o el evento es de pago único, creamos un plazo del 100%
                                    calendarioTemplate.Add(new CalendarioPagoItem
                                    {
                                        Numero_Pago = 1,
                                        Fecha_Limite = modelo.Fecha_Inicio,
                                        Monto = modelo.Costo_Entrada,
                                        Nombre_Concepto = "Pago Único"
                                    });
                                }

                                // C.3 Calcular el 100% base para poder sacar porcentajes de las modalidades
                                decimal totalTemplate = calendarioTemplate.Sum(x => x.Monto);
                                if (totalTemplate <= 0) totalTemplate = 1; // Prevención de división por 0

                                string sqlInsertCal = @"INSERT INTO ""Eventos_Calendario"" 
                                (""Id_Evento"", ""Numero_Pago"", ""Fecha_Limite"", ""Monto_Base"", ""Nombre_Concepto"", ""Id_Subtipo"") 
                                VALUES (@ev, @num, @fecha, @monto, @desc, @sub)";

                                // C.4 Guardar el Calendario "General" de la Plantilla (Id_Subtipo = NULL)
                                int numPago = 1;
                                foreach (var pago in calendarioTemplate)
                                {
                                    using (var cmd = new NpgsqlCommand(sqlInsertCal, conexion, trans))
                                    {
                                        cmd.Parameters.AddWithValue("@ev", idEvento);
                                        cmd.Parameters.AddWithValue("@num", numPago);
                                        cmd.Parameters.AddWithValue("@fecha", pago.Fecha_Limite);
                                        cmd.Parameters.AddWithValue("@monto", pago.Monto);
                                        cmd.Parameters.AddWithValue("@desc", pago.Nombre_Concepto ?? $"Pago {numPago}");
                                        cmd.Parameters.AddWithValue("@sub", DBNull.Value); // NULL significa que es el maestro
                                        await cmd.ExecuteNonQueryAsync();
                                    }
                                    numPago++;
                                }

                                // C.5 Generar Calendarios Espejo para CADA Modalidad (Subtipo)
                                var subtiposReales = new List<SubtipoEventoItem>();
                                using (var cmdS = new NpgsqlCommand(@"SELECT ""Id_Subtipo"", ""Costo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento""=@id", conexion, trans))
                                {
                                    cmdS.Parameters.AddWithValue("@id", idEvento);
                                    using (var r = await cmdS.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            subtiposReales.Add(new SubtipoEventoItem { Id_Subtipo = (int)r[0], Costo = (decimal)r[1] });
                                        }
                                    }
                                }

                                foreach (var sub in subtiposReales)
                                {
                                    decimal acumuladoGenerado = 0;
                                    int idx = 1;
                                    int totalPagos = calendarioTemplate.Count;

                                    foreach (var pagoBase in calendarioTemplate)
                                    {
                                        decimal montoCalculado = 0;

                                        if (idx == totalPagos)
                                        {
                                            // PAGO FINAL: Por seguridad matemática, el último cobro siempre será la resta exacta 
                                            // para evitar pérdida o ganancia de centavos producidos por el redondeo previo.
                                            montoCalculado = sub.Costo - acumuladoGenerado;
                                            if (montoCalculado < 0) montoCalculado = 0;
                                        }
                                        else
                                        {
                                            // PAGOS INTERMEDIOS: Regla de 3 (El plazo X equivale a qué % del costo total)
                                            decimal porcentaje = pagoBase.Monto / totalTemplate;
                                            montoCalculado = Math.Round(sub.Costo * porcentaje, 2);
                                        }

                                        using (var cmd = new NpgsqlCommand(sqlInsertCal, conexion, trans))
                                        {
                                            cmd.Parameters.AddWithValue("@ev", idEvento);
                                            cmd.Parameters.AddWithValue("@num", idx);
                                            cmd.Parameters.AddWithValue("@fecha", pagoBase.Fecha_Limite);
                                            cmd.Parameters.AddWithValue("@monto", montoCalculado);
                                            cmd.Parameters.AddWithValue("@desc", pagoBase.Nombre_Concepto ?? $"Pago {idx}");
                                            cmd.Parameters.AddWithValue("@sub", sub.Id_Subtipo); // Relacionado a esta modalidad
                                            await cmd.ExecuteNonQueryAsync();
                                        }
                                        acumuladoGenerado += montoCalculado;
                                        idx++;
                                    }
                                }

                                // ---------------------------------------------------------------------------------
                                // C.6 REGENERAR DEUDAS PARA ASISTENTES EXISTENTES
                                // Como borramos las cuentas por cobrar, debemos volver a asignarlas a los 
                                // usuarios que ya estaban inscritos usando el nuevo calendario recién creado.
                                // ---------------------------------------------------------------------------------
                                string sqlRegenerarDeudas = @"
                                INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"")
                                SELECT 
                                    b.""Id_Asistente"", 
                                    cal.""Id_Calendario"", 
                                    cal.""Monto_Base"", 
                                    FALSE
                                FROM ""Eventos_B_Asistentes"" b
                                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                                JOIN ""Eventos_Calendario"" cal ON r.""Id_Evento"" = cal.""Id_Evento""
                                WHERE r.""Id_Evento"" = @ev
                                  AND cal.""Numero_Pago"" > 0
                                  AND (
                                       (b.""Id_Subtipo"" IS NULL AND cal.""Id_Subtipo"" IS NULL) 
                                       OR 
                                       (b.""Id_Subtipo"" = cal.""Id_Subtipo"")
                                  )";

                                using (var cmdDeudas = new NpgsqlCommand(sqlRegenerarDeudas, conexion, trans))
                                {
                                    cmdDeudas.Parameters.AddWithValue("@ev", idEvento);
                                    await cmdDeudas.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                // ESCENARIO B: EL EVENTO TIENE PAGOS ACTIVOS (CANDADO FINANCIERO).
                                // No podemos borrar ni modificar el calendario existente principal porque arruinaríamos 
                                // el historial y las cuentas por cobrar de los usuarios actuales.
                                // Sin embargo, si el admin añadió una NUEVA MODALIDAD (Subtipo), necesitamos generarle
                                // su calendario "espejo" particular, de lo contrario los usuarios de esta nueva modalidad no tendrían qué pagar.

                                var calendarioTemplateDB = new List<CalendarioPagoItem>();
                                decimal totalTemplateDB = 0;

                                // C.1 Recuperar la plantilla general (maestra) directamente de la Base de Datos, ignorando los inputs del form.
                                string sqlGetTemplate = @"SELECT ""Numero_Pago"", ""Fecha_Limite"", ""Monto_Base"", ""Nombre_Concepto"" 
                                  FROM ""Eventos_Calendario"" 
                                  WHERE ""Id_Evento"" = @id AND ""Id_Subtipo"" IS NULL AND ""Numero_Pago"" > 0 
                                  ORDER BY ""Numero_Pago"" ASC";

                                using (var cmdT = new NpgsqlCommand(sqlGetTemplate, conexion, trans))
                                {
                                    cmdT.Parameters.AddWithValue("@id", idEvento);
                                    using (var r = await cmdT.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            var item = new CalendarioPagoItem
                                            {
                                                Numero_Pago = (int)r["Numero_Pago"],
                                                Fecha_Limite = (DateTime)r["Fecha_Limite"],
                                                Monto = (decimal)r["Monto_Base"],
                                                Nombre_Concepto = r["Nombre_Concepto"]?.ToString()
                                            };
                                            calendarioTemplateDB.Add(item);
                                            totalTemplateDB += item.Monto;
                                        }
                                    }
                                }

                                // Fallback de seguridad extrema: Si por alguna razón la BD perdió el calendario maestro, asumimos un pago único.
                                if (!calendarioTemplateDB.Any())
                                {
                                    calendarioTemplateDB.Add(new CalendarioPagoItem
                                    {
                                        Numero_Pago = 1,
                                        Fecha_Limite = modelo.Fecha_Inicio,
                                        Monto = modelo.Costo_Entrada,
                                        Nombre_Concepto = "Pago Único"
                                    });
                                    totalTemplateDB = modelo.Costo_Entrada;
                                }

                                if (totalTemplateDB <= 0) totalTemplateDB = 1; // Prevenir división entre cero en la regla de 3

                                // C.2 Detectar modalidades (Subtipos) recién agregadas (Están Activas pero NO existen en Eventos_Calendario)
                                var subtiposSinCalendario = new List<SubtipoEventoItem>();
                                string sqlMissing = @"
                    SELECT s.""Id_Subtipo"", s.""Costo"" 
                    FROM ""Eventos_Subtipos"" s 
                    WHERE s.""Id_Evento"" = @id 
                      AND s.""Activo"" = TRUE 
                      AND NOT EXISTS (
                          SELECT 1 FROM ""Eventos_Calendario"" c WHERE c.""Id_Subtipo"" = s.""Id_Subtipo""
                      )";

                                using (var cmdM = new NpgsqlCommand(sqlMissing, conexion, trans))
                                {
                                    cmdM.Parameters.AddWithValue("@id", idEvento);
                                    using (var r = await cmdM.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            subtiposSinCalendario.Add(new SubtipoEventoItem
                                            {
                                                Id_Subtipo = (int)r["Id_Subtipo"],
                                                Costo = (decimal)r["Costo"]
                                            });
                                        }
                                    }
                                }

                                // C.3 Insertar el calendario espejo ÚNICAMENTE para las modalidades nuevas detectadas, respetando el resto del evento
                                if (subtiposSinCalendario.Any())
                                {
                                    string sqlInsertCal = @"INSERT INTO ""Eventos_Calendario"" 
                            (""Id_Evento"", ""Numero_Pago"", ""Fecha_Limite"", ""Monto_Base"", ""Nombre_Concepto"", ""Id_Subtipo"") 
                            VALUES (@ev, @num, @fecha, @monto, @desc, @sub)";

                                    foreach (var sub in subtiposSinCalendario)
                                    {
                                        decimal acumuladoGenerado = 0;
                                        int idx = 1;
                                        int totalPagos = calendarioTemplateDB.Count;

                                        foreach (var pagoBase in calendarioTemplateDB)
                                        {
                                            decimal montoCalculado = 0;

                                            if (idx == totalPagos)
                                            {
                                                // Ajuste de centavos final
                                                montoCalculado = sub.Costo - acumuladoGenerado;
                                                if (montoCalculado < 0) montoCalculado = 0;
                                            }
                                            else
                                            {
                                                // Regla de 3 para calcular proporción
                                                decimal porcentaje = pagoBase.Monto / totalTemplateDB;
                                                montoCalculado = Math.Round(sub.Costo * porcentaje, 2);
                                            }

                                            using (var cmd = new NpgsqlCommand(sqlInsertCal, conexion, trans))
                                            {
                                                cmd.Parameters.AddWithValue("@ev", idEvento);
                                                cmd.Parameters.AddWithValue("@num", idx);
                                                cmd.Parameters.AddWithValue("@fecha", pagoBase.Fecha_Limite);
                                                cmd.Parameters.AddWithValue("@monto", montoCalculado);
                                                cmd.Parameters.AddWithValue("@desc", pagoBase.Nombre_Concepto ?? $"Pago {idx}");
                                                cmd.Parameters.AddWithValue("@sub", sub.Id_Subtipo); // Este insert es exclusivo para esta nueva modalidad
                                                await cmd.ExecuteNonQueryAsync();
                                            }
                                            acumuladoGenerado += montoCalculado;
                                            idx++;
                                        }
                                    }
                                }
                            }

                            long existeOculto = 0;
                            using (var cmdCheckOculto = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @ev AND ""Numero_Pago"" = -1", conexion, trans))
                            {
                                cmdCheckOculto.Parameters.AddWithValue("@ev", idEvento);
                                existeOculto = (long)await cmdCheckOculto.ExecuteScalarAsync();
                            }

                            if (existeOculto == 0)
                            {
                                // 1. Buscar la fecha máxima de los pagos recién guardados/existentes
                                DateTime fechaMaximaBase = modelo.Fecha_Inicio.AddDays(1); // Fallback por defecto

                                string sqlMaxFecha = @"SELECT MAX(""Fecha_Limite"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @ev AND ""Numero_Pago"" > 0";
                                using (var cmdMax = new NpgsqlCommand(sqlMaxFecha, conexion, trans))
                                {
                                    cmdMax.Parameters.AddWithValue("@ev", idEvento);
                                    var resMax = await cmdMax.ExecuteScalarAsync();

                                    if (resMax != null && resMax != DBNull.Value)
                                    {
                                        fechaMaximaBase = Convert.ToDateTime(resMax).AddDays(1); // Exactamente un día después del último pago
                                    }
                                }

                                // 2. Insertar el calendario oculto con la fecha calculada
                                string sqlCalendarioOculto = @"INSERT INTO ""Eventos_Calendario"" 
                            (""Id_Evento"", ""Numero_Pago"", ""Fecha_Limite"", ""Monto_Base"", ""Nombre_Concepto"", ""Id_Subtipo"") 
                            VALUES (@ev, -1, @fecha, 0, 'Ajuste de Modalidad', NULL)";

                                using (var cmdInsOculto = new NpgsqlCommand(sqlCalendarioOculto, conexion, trans))
                                {
                                    cmdInsOculto.Parameters.AddWithValue("@ev", idEvento);
                                    cmdInsOculto.Parameters.AddWithValue("@fecha", fechaMaximaBase);
                                    await cmdInsOculto.ExecuteNonQueryAsync();
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO D: ALMACENAMIENTO DE DATOS COMPLEMENTARIOS (PREGUNTAS, GALERÍA, PORTADA)
                            // ---------------------------------------------------------------------------------

                            if (modelo.Preguntas != null)
                            {
                                foreach (var p in modelo.Preguntas)
                                {
                                    if (p.Eliminada)
                                    {
                                        if (p.Id_Pregunta > 0)
                                        {
                                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Pregunta\"={p.Id_Pregunta}", conexion, trans).ExecuteNonQueryAsync();
                                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Preguntas\" WHERE \"Id_Pregunta\"={p.Id_Pregunta}", conexion, trans).ExecuteNonQueryAsync();
                                        }
                                        continue;
                                    }

                                    if (string.IsNullOrWhiteSpace(p.Texto)) continue;

                                    object opc = string.IsNullOrWhiteSpace(p.Opciones) ? DBNull.Value : (object)p.Opciones;

                                    if (p.Id_Pregunta == 0)
                                    {
                                        string sqlIns = @"INSERT INTO ""Eventos_Preguntas"" 
                                  (""Id_Evento"", ""Texto_Pregunta"", ""Tipo_Pregunta"", ""Opciones"") 
                                  VALUES (@ev, @txt, @tipo, @opc)";
                                        using (var cmdP = new NpgsqlCommand(sqlIns, conexion, trans))
                                        {
                                            cmdP.Parameters.AddWithValue("@ev", idEvento);
                                            cmdP.Parameters.AddWithValue("@txt", p.Texto);
                                            cmdP.Parameters.AddWithValue("@tipo", (int)p.Tipo);
                                            cmdP.Parameters.AddWithValue("@opc", opc);
                                            await cmdP.ExecuteNonQueryAsync();
                                        }
                                    }
                                    else
                                    {
                                        string sqlUpd = @"UPDATE ""Eventos_Preguntas"" 
                                  SET ""Texto_Pregunta""=@txt, ""Tipo_Pregunta""=@tipo, ""Opciones""=@opc 
                                  WHERE ""Id_Pregunta""=@id";
                                        using (var cmdP = new NpgsqlCommand(sqlUpd, conexion, trans))
                                        {
                                            cmdP.Parameters.AddWithValue("@txt", p.Texto);
                                            cmdP.Parameters.AddWithValue("@tipo", (int)p.Tipo);
                                            cmdP.Parameters.AddWithValue("@opc", opc);
                                            cmdP.Parameters.AddWithValue("@id", p.Id_Pregunta);
                                            await cmdP.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // Subir Imagen de Portada Principal y Destruir la vieja en Cloudinary
                            if (fotoPortadaNueva != null)
                            {
                                string urlVieja = "";
                                using (var cmdOld = new NpgsqlCommand($"SELECT \"Imagen_Url\" FROM \"Eventos_Catalogo\" WHERE \"Id_Evento\" = {idEvento}", conexion, trans))
                                {
                                    var resObj = await cmdOld.ExecuteScalarAsync();
                                    if (resObj != null && resObj != DBNull.Value) urlVieja = resObj.ToString();
                                }

                                string urlPortada = await SubirImagenCloudinary(fotoPortadaNueva, $"{sAmbiente}/Imágenes/Eventos/{idEvento}");
                                await new NpgsqlCommand($"UPDATE \"Eventos_Catalogo\" SET \"Imagen_Url\" = '{urlPortada}' WHERE \"Id_Evento\" = {idEvento}", conexion, trans).ExecuteNonQueryAsync();

                                if (!string.IsNullOrEmpty(urlVieja) && urlVieja.Contains("cloudinary.com"))
                                {
                                    try
                                    {
                                        var uriOld = new Uri(urlVieja);
                                        var segmentsOld = uriOld.Segments;
                                        int uploadIndexOld = Array.IndexOf(segmentsOld, "upload/");
                                        if (uploadIndexOld >= 0 && segmentsOld.Length > uploadIndexOld + 2)
                                        {
                                            string publicIdOldExt = string.Join("", segmentsOld.Skip(uploadIndexOld + 2));
                                            string publicIdOld = Path.ChangeExtension(publicIdOldExt, null).Replace("%20", " ").Trim('/');
                                            await _cloudinary.DestroyAsync(new DeletionParams(publicIdOld));
                                        }
                                    }
                                    catch { /* Se ignora si falla para no interrumpir el guardado de la DB */ }
                                }
                            }

                            // Gestionar Galería Múltiple (Borrados, Actualizaciones de Orden y Nuevas Fotos)
                            if (imagenesAEliminar != null && imagenesAEliminar.Count > 0)
                            {
                                foreach (var urlImg in imagenesAEliminar)
                                {
                                    using (var cmdDelImg = new NpgsqlCommand("DELETE FROM \"Eventos_Galeria\" WHERE \"Id_Evento\" = @id AND \"Url_Foto\" = @url", conexion, trans))
                                    {
                                        cmdDelImg.Parameters.AddWithValue("@id", idEvento);
                                        cmdDelImg.Parameters.AddWithValue("@url", urlImg);
                                        await cmdDelImg.ExecuteNonQueryAsync();
                                    }

                                    try
                                    {
                                        var uri = new Uri(urlImg);
                                        var segments = uri.Segments;
                                        int uploadIndex = Array.IndexOf(segments, "upload/");
                                        if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                                        {
                                            string publicIdWithExtension = string.Join("", segments.Skip(uploadIndex + 2));
                                            string publicId = Path.ChangeExtension(publicIdWithExtension, null).Replace("%20", " ").Trim('/');
                                            await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                                        }
                                    }
                                    catch { }
                                }
                            }

                            int ordenGlobal = 1;
                            if (modelo.Galeria != null)
                            {
                                foreach (var g in modelo.Galeria)
                                {
                                    string sqlUpdG = @"UPDATE ""Eventos_Galeria"" SET ""Descripcion"" = @desc, ""Orden"" = @ord WHERE ""Id_Foto"" = @idF";
                                    using (var cmdUpdG = new NpgsqlCommand(sqlUpdG, conexion, trans))
                                    {
                                        cmdUpdG.Parameters.AddWithValue("@desc", (object)g.Descripcion ?? DBNull.Value);
                                        cmdUpdG.Parameters.AddWithValue("@ord", ordenGlobal++);
                                        cmdUpdG.Parameters.AddWithValue("@idF", g.Id_Foto);
                                        await cmdUpdG.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            if (nuevasFotosGaleria != null && nuevasFotosGaleria.Count > 0)
                            {
                                for (int i = 0; i < nuevasFotosGaleria.Count; i++)
                                {
                                    var foto = nuevasFotosGaleria[i];
                                    string desc = (descripcionesNuevasFotos != null && descripcionesNuevasFotos.Count > i && !string.IsNullOrWhiteSpace(descripcionesNuevasFotos[i]))
                                                    ? descripcionesNuevasFotos[i] : null;

                                    string urlFinal = await SubirImagenCloudinary(foto, $"{sAmbiente}/Imágenes/Eventos/{idEvento}");

                                    using (var cmdG = new NpgsqlCommand("INSERT INTO \"Eventos_Galeria\" (\"Id_Evento\", \"Url_Foto\", \"Orden\", \"Descripcion\") VALUES (@ev, @url, @ord, @desc)", conexion, trans))
                                    {
                                        cmdG.Parameters.AddWithValue("@ev", idEvento);
                                        cmdG.Parameters.AddWithValue("@url", urlFinal);
                                        cmdG.Parameters.AddWithValue("@ord", ordenGlobal++);
                                        cmdG.Parameters.AddWithValue("@desc", (object)desc ?? DBNull.Value);
                                        await cmdG.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // Gestión de Permisos de Privacidad (Inscriptores Exclusivos)
                            if (modelo.Es_Privado)
                            {
                                var actualesDB = new List<int>();
                                using (var cmdList = new NpgsqlCommand($"SELECT \"Id_Usuario\" FROM \"Eventos_Inscriptores\" WHERE \"Id_Evento\"={idEvento}", conexion, trans))
                                {
                                    using (var r = await cmdList.ExecuteReaderAsync()) while (await r.ReadAsync()) actualesDB.Add((int)r[0]);
                                }

                                var nuevosUI = new List<int>();
                                if (!string.IsNullOrEmpty(modelo.IdsInscriptores))
                                {
                                    nuevosUI = modelo.IdsInscriptores.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x)).ToList();
                                }

                                var borrar = actualesDB.Except(nuevosUI).ToList();
                                var agregar = nuevosUI.Except(actualesDB).ToList();

                                var ActualesConRegistros = new List<int>();
                                string sqlReg = @"SELECT DISTINCT ""Id_Usuario"" FROM ""Eventos_A_Registros"" WHERE ""Id_Evento""=@ev";
                                using (var cmdR = new NpgsqlCommand(sqlReg, conexion, trans))
                                {
                                    cmdR.Parameters.AddWithValue("@ev", idEvento);
                                    using (var r = await cmdR.ExecuteReaderAsync()) while (await r.ReadAsync()) ActualesConRegistros.Add((int)r[0]);
                                }

                                // Evitar quitar permisos a usuarios que ya tienen registros capturados
                                if (ActualesConRegistros.Any())
                                {
                                    borrar = borrar.Except(ActualesConRegistros).ToList();
                                }

                                if (borrar.Any())
                                {

                                    string sqlDel = @"DELETE FROM ""Eventos_Inscriptores"" WHERE ""Id_Evento""=@ev AND ""Id_Usuario""=@uid";
                                    foreach (var uid in borrar)
                                    {
                                        using (var cmdD = new NpgsqlCommand(sqlDel, conexion, trans))
                                        {
                                            cmdD.Parameters.AddWithValue("@ev", idEvento);
                                            cmdD.Parameters.AddWithValue("@uid", uid);
                                            await cmdD.ExecuteNonQueryAsync();
                                        }
                                    }
                                }

                                if (agregar.Any())
                                {
                                    string sqlIns = @"INSERT INTO ""Eventos_Inscriptores"" (""Id_Evento"", ""Id_Usuario"") VALUES (@ev, @uid)";
                                    foreach (var uid in agregar)
                                    {
                                        using (var cmdI = new NpgsqlCommand(sqlIns, conexion, trans))
                                        {
                                            cmdI.Parameters.AddWithValue("@ev", idEvento);
                                            cmdI.Parameters.AddWithValue("@uid", uid);
                                            await cmdI.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // ---------------------------------------------------------------------------------
                            // PASO E: BITÁCORA Y CONFIRMACIÓN TRANSACCIONAL
                            // ---------------------------------------------------------------------------------
                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            var accion = modelo.Id_Evento == 0 ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar;
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, accion, $"Evento #{idEvento} guardado.", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }

                    if (modelo.Id_Evento > 0)
                    {
                        MostrarMensaje("Guardado", "Evento actualizado correctamente.", TipoMensaje.Exito);
                    }
                    else
                    {
                        MostrarMensaje("Creado", "Evento creado correctamente.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Helper para asignar parámetros comunes al comando SQL de Guardar Evento.
        /// </summary>
        private void SetParams(NpgsqlCommand cmd, EditorEventoViewModel m)
        {
            cmd.Parameters.AddWithValue("@tit", m.Titulo);
            cmd.Parameters.AddWithValue("@desc", m.Descripcion ?? "");
            cmd.Parameters.AddWithValue("@ini", m.Fecha_Inicio);
            cmd.Parameters.AddWithValue("@fin", m.Fecha_Fin);
            cmd.Parameters.AddWithValue("@costo", m.Costo_Entrada);
            cmd.Parameters.AddWithValue("@req", m.Requiere_Registro);
            cmd.Parameters.AddWithValue("@tipo", m.Tipo_Registro);
            cmd.Parameters.AddWithValue("@cupo", m.Cupo_Maximo);
            cmd.Parameters.AddWithValue("@img", m.Imagen_Url ?? "");
            cmd.Parameters.AddWithValue("@dest", m.Es_Destacado);
            cmd.Parameters.AddWithValue("@act", m.Activo);
            cmd.Parameters.AddWithValue("@pago", m.Permitir_Pago);
            cmd.Parameters.AddWithValue("@claveBanco", m.Clave_Cuenta_Bancaria ?? "DEFAULT");
            object idGrupoDB = m.Id_Grupo_Usuarios > 0 ? m.Id_Grupo_Usuarios : DBNull.Value;
            cmd.Parameters.AddWithValue("@idGrupo", idGrupoDB);
        }

        /// <summary>
        /// POST: Genera un intento de transferencia bancaria para los pagos seleccionados.
        /// </summary>
        /// <param name="idsPagosSeleccionados">Es una lista de IDs de las cuentas por cobrar seleccionadas.</param>
        /// <param name="token">Es un token opcional para identificar la sesión o contexto.</param>
        /// <param name="idEvento">Es el ID del evento asociado a los pagos.</param>
        /// <returns>Redirecciona a la vista para subir el comprobante de transferencia.</returns>
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerarIntentoTransferencia(List<string> idsPagosSeleccionados, string token, int idEvento, string codigoCupon = "")
        {
            if (idsPagosSeleccionados == null || !idsPagosSeleccionados.Any())
                return RedirectToAction("Index");

            if (!string.IsNullOrWhiteSpace(codigoCupon) && !User.Identity.IsAuthenticated)
            {
                MostrarMensaje("Acceso Requerido", "Para usar cupones en transferencias debes iniciar sesión.", TipoMensaje.Alerta);
                return RedirectToAction("Pagar", new { token = token });
            }

            int idUserOwner = User.Identity.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;
            decimal totalReal = 0;
            int idTransaccionCreada = 0;

            var idsInts = new List<int>();
            foreach (var s in idsPagosSeleccionados) if (int.TryParse(s, out int n)) idsInts.Add(n);
            if (!idsInts.Any())
            {
                MostrarMensaje("Error", "No has seleccionado ningún pago", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // --- MÉTODO DE PAGO PERMITIDO ---
                            bool permiteTransferencia = false;
                            string sqlMetodoTransf = @"SELECT ""Permitir_Transferencia"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @idEv";
                            using (var cmdMetodo = new NpgsqlCommand(sqlMetodoTransf, conexion, trans))
                            {
                                cmdMetodo.Parameters.AddWithValue("@idEv", idEvento);
                                var resObj = await cmdMetodo.ExecuteScalarAsync();
                                if (resObj != null && resObj != DBNull.Value) permiteTransferencia = (bool)resObj;
                            }

                            if (!permiteTransferencia)
                            {
                                throw new UnauthorizedAccessException("El pago por transferencia bancaria no está habilitado para este evento.");
                            }

                            var idsValidosCuentas = new List<int>();
                            var idsAsistentesPagar = new HashSet<int>(); // NUEVO: Usamos HashSet para evitar duplicados si pagan varias cuentas de la misma persona
                            string nombreParaReferencia = "Usuario";

                            // Agregamos b."Id_Asistente" a la consulta para extraerlo
                            string sqlValidacion = @"
                                SELECT c.""Id_Cuenta"", c.""Monto_Pagar"", b.""Nombre_Completo"", b.""Id_Asistente""
                                FROM ""Eventos_C_Cuentas_Cobrar"" c
                                JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                                WHERE c.""Id_Cuenta"" = ANY(@ids) AND c.""Pagado"" = FALSE
                                AND (
                                    (@isGuest = TRUE AND b.""Token_Pago_Externo"" = @tok) OR 
                                    (@isGuest = FALSE AND r.""Id_Usuario"" = @uid)
                                )";

                            using (var cmdVal = new NpgsqlCommand(sqlValidacion, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@ids", idsInts);
                                cmdVal.Parameters.AddWithValue("@isGuest", idUserOwner == 0);
                                cmdVal.Parameters.AddWithValue("@tok", token ?? "");
                                cmdVal.Parameters.AddWithValue("@uid", idUserOwner);

                                using (var r = await cmdVal.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync())
                                    {
                                        idsValidosCuentas.Add((int)r["Id_Cuenta"]);
                                        idsAsistentesPagar.Add((int)r["Id_Asistente"]); // Recolectamos a los asistentes
                                        totalReal += (decimal)r["Monto_Pagar"];
                                        nombreParaReferencia = r["Nombre_Completo"].ToString();
                                    }
                                }
                            }

                            if (!idsValidosCuentas.Any() || totalReal <= 0)
                                throw new UnauthorizedAccessException("Cuentas inválidas, manipuladas o ya pagadas.");

                            // =======================================================================
                            // --- BLOQUEO DE SEGURIDAD CONTRA SOBREVENTAS (TRANSFERENCIAS) ---
                            // =======================================================================
                            var asistentesExcluir = idsAsistentesPagar.ToList();
                            var disp = await ConsultarDisponibilidadEvento(idEvento, conexion, trans, true, asistentesExcluir);

                            if (disp != null)
                            {
                                // Si hay modalidades, se desactiva el cupo global y se valida estrictamente cada modalidad del grupo que paga.
                                if (disp.Modalidades != null && disp.Modalidades.Any())
                                {
                                    string sqlSubAsis = @"SELECT ""Id_Subtipo"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                                    var subtiposPagar = new List<int>();
                                    using (var cmdSubAsis = new NpgsqlCommand(sqlSubAsis, conexion, trans))
                                    {
                                        cmdSubAsis.Parameters.AddWithValue("@ids", asistentesExcluir);
                                        using (var rSub = await cmdSubAsis.ExecuteReaderAsync())
                                        {
                                            while (await rSub.ReadAsync())
                                            {
                                                if (rSub[0] != DBNull.Value) subtiposPagar.Add(Convert.ToInt32(rSub[0]));
                                            }
                                        }
                                    }

                                    var conteoPorModalidad = subtiposPagar.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

                                    foreach (var kvp in conteoPorModalidad)
                                    {
                                        var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == kvp.Key);
                                        if (modal != null && modal.LugaresDisponibles < kvp.Value)
                                        {
                                            throw new Exception($"Tu tiempo de reserva de 30 minutos expiró y los lugares para la modalidad '{modal.NombreModalidad}' ya fueron ocupados por alguien más.");
                                        }
                                    }
                                }
                                else
                                {
                                    // Si NO tiene modalidades, el cupo global hace su función normal
                                    if (disp.CupoMaximoEvento > 0 && disp.DisponiblesGlobal < asistentesExcluir.Count)
                                    {
                                        throw new Exception("Tu tiempo de reserva de 30 minutos expiró y los lugares globales ya fueron ocupados por alguien más.");
                                    }
                                }
                            }
                            // --- LÓGICA CUPONES (Validación Server-Side) ---
                            decimal descuentoAplicado = 0;
                            int? idCuponAplicado = null;
                            string codigoGuardar = null;

                            if (!string.IsNullOrEmpty(codigoCupon))
                            {
                                string sqlCupon = @"SELECT c.*,
                        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"") as ""TotalLista"",
                        (SELECT COUNT(*) FROM ""Sist_Cupones_Usuarios"" u WHERE u.""Id_Cupon"" = c.""Id_Cupon"" AND u.""Id_Usuario"" = @uid) as ""TengoPermiso""
                        FROM ""Sist_Cupones"" c 
                        WHERE UPPER(c.""Codigo"") = UPPER(@cod) 
                        AND c.""Activo"" = TRUE 
                        AND NOW() BETWEEN c.""Fecha_Inicio"" AND c.""Fecha_Fin"" 
                        FOR UPDATE";

                                using (var cmdC = new NpgsqlCommand(sqlCupon, conexion, trans))
                                {
                                    cmdC.Parameters.AddWithValue("@cod", codigoCupon.Trim());
                                    cmdC.Parameters.AddWithValue("@uid", idUserOwner);

                                    using (var rC = await cmdC.ExecuteReaderAsync())
                                    {
                                        if (await rC.ReadAsync())
                                        {
                                            int limite = (int)rC["Limite_Usos"];
                                            int usados = (int)rC["Conteo_Usados"];
                                            decimal minimo = (decimal)rC["Monto_Minimo_Compra"];
                                            bool aplicaEventos = (bool)rC["Aplica_Eventos"];
                                            int? eventoRestringido = rC["Id_Evento_Restringido"] as int?;

                                            bool tieneLista = Convert.ToInt64(rC["TotalLista"]) > 0;
                                            bool tengoPermiso = Convert.ToInt64(rC["TengoPermiso"]) > 0;

                                            bool stockOk = usados < limite;
                                            bool montoOk = totalReal >= minimo;
                                            bool eventoOk = !aplicaEventos || !eventoRestringido.HasValue || eventoRestringido.Value == idEvento;

                                            bool usuarioOk = true;
                                            if (tieneLista && !tengoPermiso) usuarioOk = false;

                                            if (stockOk && montoOk && eventoOk && usuarioOk)
                                            {
                                                int tipo = (int)rC["Tipo_Descuento"];
                                                decimal valor = (decimal)rC["Valor"];
                                                decimal? tope = rC["Tope_Maximo_Descuento"] as decimal?;

                                                descuentoAplicado = Funciones.CalcularMontoDescuento(totalReal, tipo, valor, tope);
                                                idCuponAplicado = (int)rC["Id_Cupon"];
                                                codigoGuardar = codigoCupon.ToUpper();
                                            }
                                        }
                                    }
                                }

                                if (idCuponAplicado.HasValue)
                                {
                                    await new NpgsqlCommand($"UPDATE \"Sist_Cupones\" SET \"Conteo_Usados\" = \"Conteo_Usados\" + 1 WHERE \"Id_Cupon\" = {idCuponAplicado}", conexion, trans).ExecuteNonQueryAsync();
                                }
                                else
                                {
                                    throw new Exception("El cupón ingresado se agotó o ya no es válido. Actualiza la página e intenta sin cupón.");
                                }
                            }

                            decimal totalFinal = totalReal - descuentoAplicado;
                            if (totalFinal < 0) totalFinal = 0;

                            // A. Crear Transacción 'waiting_proof'
                            string refTemp = "TRANSFERENCIA";
                            string sqlTrx = @"INSERT INTO ""Eventos_D_Transacciones"" 
                                (""Id_Usuario"", ""Id_Evento"", ""Monto_Total"", ""Estatus_Pago"", ""Fecha_Intento"", ""Ref_Pasarela"", ""EsTransferencia"", ""Id_Cupon"", ""Codigo_Cupon_Aplicado"", ""Monto_Descuento"")
                                VALUES (@usr, @ev, @monto, 'waiting_proof', NOW(), @ref, TRUE, @idC, @codC, @montC) 
                                RETURNING ""Id_Transaccion""";

                            using (var cmd = new NpgsqlCommand(sqlTrx, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@usr", idUserOwner);
                                cmd.Parameters.AddWithValue("@ev", idEvento);
                                cmd.Parameters.AddWithValue("@monto", totalFinal);
                                cmd.Parameters.AddWithValue("@ref", refTemp);
                                cmd.Parameters.AddWithValue("@idC", (object)idCuponAplicado ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@codC", (object)codigoGuardar ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@montC", descuentoAplicado);
                                idTransaccionCreada = (int)await cmd.ExecuteScalarAsync();
                            }

                            // --- Generar y guardar la Referencia Externa ---
                            string primerNombre = nombreParaReferencia.Trim().Split(' ')[0];
                            primerNombre = System.Text.RegularExpressions.Regex.Replace(primerNombre, "[^a-zA-Z0-9]", "");
                            string referenciaFinal = $"{sPrefijoTransfer}{primerNombre}{idTransaccionCreada}".ToUpper();

                            string sqlUpdRef = @"UPDATE ""Eventos_D_Transacciones"" SET ""External_Reference"" = @extRef WHERE ""Id_Transaccion"" = @idT";
                            using (var cmdUpdRef = new NpgsqlCommand(sqlUpdRef, conexion, trans))
                            {
                                cmdUpdRef.Parameters.AddWithValue("@extRef", referenciaFinal);
                                cmdUpdRef.Parameters.AddWithValue("@idT", idTransaccionCreada);
                                await cmdUpdRef.ExecuteNonQueryAsync();
                            }
                            // -------------------------------------------------------

                            if (idCuponAplicado.HasValue && idUserOwner > 0)
                            {
                                string sqlUso = @"INSERT INTO ""Sist_Cupones_Uso"" (""Id_Cupon"", ""Id_Usuario"", ""Id_Transaccion_Evento"", ""Fecha_Uso"") 
                                          VALUES (@idC, @idU, @idTrx, NOW())";
                                using (var cmdUso = new NpgsqlCommand(sqlUso, conexion, trans))
                                {
                                    cmdUso.Parameters.AddWithValue("@idC", idCuponAplicado);
                                    cmdUso.Parameters.AddWithValue("@idU", idUserOwner);
                                    cmdUso.Parameters.AddWithValue("@idTrx", idTransaccionCreada);
                                    await cmdUso.ExecuteNonQueryAsync();
                                }
                            }

                            // B. Guardar el detalle
                            string sqlE = @"INSERT INTO ""Eventos_E_Pagos_Aplicados"" (""Id_Transaccion"", ""Id_Cuenta"") VALUES (@tr, @cta)";
                            foreach (int idCuentaSegura in idsValidosCuentas)
                            {
                                using (var cmdE = new NpgsqlCommand(sqlE, conexion, trans))
                                {
                                    cmdE.Parameters.AddWithValue("@tr", idTransaccionCreada);
                                    cmdE.Parameters.AddWithValue("@cta", idCuentaSegura);
                                    await cmdE.ExecuteNonQueryAsync();
                                }
                            }

                            await Funciones.RegistrarBitacora(conexion, idUserOwner, Modulo,
                                Parametros.AccionesBitacora.Crear,
                                $"Generó Orden de Transferencia #{idTransaccionCreada} por ${totalReal:N2}. Ref: {referenciaFinal}",
                                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            return RedirectToAction("CargarComprobante", new { idTransaccion = idTransaccionCreada, token = token });
        }

        /// <summary>
        /// Muestra la pantalla para subir comprobante. Carga la información bancaria específica del evento.
        /// </summary>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> CargarComprobante(int idTransaccion, string token = null)
        {
            if (idTransaccion <= 0)
            {
                MostrarMensaje("Error", "No se ha podido identificar la transacción", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new SubirComprobanteViewModel { Token = token };
            modelo.IdsPagosSeleccionados = new List<string> { idTransaccion.ToString() };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Validar transacción y OBTENER DATOS (Agregado External_Reference)
                    string sql = @"SELECT d.""Monto_Total"", d.""Estatus_Pago"", d.""Id_Evento"", 
                                          d.""Monto_Descuento"", d.""Codigo_Cupon_Aplicado"", d.""External_Reference"",
                                          e.""Clave_Cuenta_Bancaria"", e.""Titulo"" 
                                   FROM ""Eventos_D_Transacciones"" d
                                   JOIN ""Eventos_Catalogo"" e ON d.""Id_Evento"" = e.""Id_Evento""
                                   WHERE d.""Id_Transaccion"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idTransaccion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                string estatus = r["Estatus_Pago"].ToString();
                                if (estatus != "waiting_proof" && estatus != "review")
                                {
                                    MostrarMensaje("Aviso", "Esta transacción ya no requiere comprobante.", TipoMensaje.Info);
                                    return RedirectToAction("Index");
                                }

                                modelo.TotalPagar = (decimal)r["Monto_Total"];
                                modelo.MontoDescuento = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0;
                                modelo.CodigoCupon = r["Codigo_Cupon_Aplicado"]?.ToString();
                                modelo.SubTotalOriginal = modelo.TotalPagar + modelo.MontoDescuento;
                                modelo.TituloEvento = r["Titulo"].ToString();
                                modelo.ClaveCuentaBancaria = r["Clave_Cuenta_Bancaria"]?.ToString() ?? "DEFAULT";

                                // Asignar la referencia guardada en DB
                                ViewBag.ReferenciaPago = r["External_Reference"]?.ToString() ?? $"TRX-{idTransaccion}";
                            }
                            else
                            {
                                MostrarMensaje("Error", "No se encontró la transacción seleccionada.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 2. Cargar detalle visual
                    modelo.Detalles = new List<DetalleConceptoPago>();
                    string sqlDet = @"
                        SELECT c.""Monto_Pagar"", cal.""Nombre_Concepto"", cal.""Numero_Pago"", 
                               b.""Nombre_Completo""
                        FROM ""Eventos_E_Pagos_Aplicados"" pa
                        JOIN ""Eventos_C_Cuentas_Cobrar"" c ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                        JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                        JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                        WHERE pa.""Id_Transaccion"" = @id
                        ORDER BY b.""Nombre_Completo"", cal.""Numero_Pago""";

                    using (var cmd = new NpgsqlCommand(sqlDet, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idTransaccion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string desc = r["Nombre_Concepto"].ToString();
                                if (string.IsNullOrEmpty(desc)) desc = $"Pago #{r["Numero_Pago"]}";

                                modelo.Detalles.Add(new DetalleConceptoPago
                                {
                                    Concepto = desc,
                                    Monto = (decimal)r["Monto_Pagar"],
                                    Persona = r["Nombre_Completo"].ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        /// <summary>
        /// Calcula el total real de los pagos seleccionados desde la base de datos.
        /// </summary>
        /// <param name="idsCuentas">Es una lista de IDs de las cuentas por cobrar.</param>
        /// <returns>Retorna el total calculado como decimal.</returns>
        private async Task<decimal> CalcularTotalDesdeBD(List<string> idsCuentas)
        {
            decimal total = 0;
            if (idsCuentas == null || !idsCuentas.Any()) return 0;

            // Convertir a enteros válidos pues la lista viene como strings
            var idsInts = new List<int>();
            foreach (var s in idsCuentas) if (int.TryParse(s, out int n)) idsInts.Add(n);

            if (!idsInts.Any()) return 0;

            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();
                string sql = @"SELECT SUM(""Monto_Pagar"") FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Cuenta"" = ANY(@ids)";
                using (var cmd = new NpgsqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@ids", idsInts);
                    var res = await cmd.ExecuteScalarAsync();
                    if (res != null && res != DBNull.Value) total = (decimal)res;
                }
            }
            return total;
        }

        /// <summary>
        /// Muestra la pantalla para retomar una transferencia bancaria previamente iniciada.
        /// </summary>
        /// <param name="idTransaccion">ES el ID de la transacción a retomar.</param>
        /// <param name="token">Es un token opcional para identificar la sesión o contexto.</param>
        /// <returns>Redirige a la vista para subir el comprobante de transferencia.</returns>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> RetomarTransferencia(int idTransaccion, string token = null)
        {
            if (idTransaccion <= 0)
            {
                MostrarMensaje("Error", "No se ha recibido el identificador de la transacción", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new SubirComprobanteViewModel { Token = token };
            modelo.IdsPagosSeleccionados = new List<string> { idTransaccion.ToString() };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Consultar encabezado Y DATOS DEL EVENTO (Importante para la cuenta bancaria)
                    string sql = @"
                        SELECT d.""Monto_Total"", d.""Estatus_Pago"", d.""Comentarios_Revision"",
                               e.""Titulo"", e.""Clave_Cuenta_Bancaria""
                        FROM ""Eventos_D_Transacciones"" d
                        JOIN ""Eventos_Catalogo"" e ON d.""Id_Evento"" = e.""Id_Evento""
                        WHERE d.""Id_Transaccion"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idTransaccion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                string estatus = r["Estatus_Pago"].ToString();

                                if (estatus != "rejected" && estatus != "waiting_proof")
                                {
                                    // Si ya está en revisión o pagado, sacar de aquí
                                    MostrarMensaje("Aviso", "Este pago ya se encuentra en revisión o finalizado.", TipoMensaje.Info);
                                    return RedirectToAction("Index");
                                }

                                modelo.TotalPagar = (decimal)r["Monto_Total"];
                                modelo.TituloEvento = r["Titulo"].ToString();
                                modelo.ClaveCuentaBancaria = r["Clave_Cuenta_Bancaria"]?.ToString() ?? "DEFAULT";

                                // Datos para la alerta visual en la vista
                                ViewBag.Estatus = estatus;
                                ViewBag.MotivoRechazo = r["Comentarios_Revision"] != DBNull.Value
                                                        ? r["Comentarios_Revision"].ToString()
                                                        : "Sin motivo especificado.";
                                ViewBag.IdTransaccion = idTransaccion;
                            }
                            else
                            {
                                MostrarMensaje("Error", "La transacción solicitada no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 2. Cargar detalle de conceptos (Personas y montos)
                    modelo.Detalles = new List<DetalleConceptoPago>();
                    string sqlDet = @"
                        SELECT c.""Monto_Pagar"", cal.""Nombre_Concepto"", cal.""Numero_Pago"", b.""Nombre_Completo""
                        FROM ""Eventos_E_Pagos_Aplicados"" pa
                        JOIN ""Eventos_C_Cuentas_Cobrar"" c ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                        JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                        JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                        WHERE pa.""Id_Transaccion"" = @id
                        ORDER BY b.""Nombre_Completo"", cal.""Numero_Pago""";

                    using (var cmd = new NpgsqlCommand(sqlDet, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idTransaccion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string desc = r["Nombre_Concepto"].ToString();
                                if (string.IsNullOrEmpty(desc)) desc = $"Pago #{r["Numero_Pago"]}";

                                modelo.Detalles.Add(new DetalleConceptoPago
                                {
                                    Concepto = desc,
                                    Monto = (decimal)r["Monto_Pagar"],
                                    Persona = r["Nombre_Completo"].ToString()
                                });
                            }
                        }
                    }
                }

                string nombreReferencia = "Usuario";

                if (modelo.Detalles != null && modelo.Detalles.Any())
                {
                    nombreReferencia = modelo.Detalles.First().Persona;
                }

                // Limpieza del nombre para generar EVT PrimerNombre ID
                string primerNombre = nombreReferencia.Trim().Split(' ')[0];
                primerNombre = System.Text.RegularExpressions.Regex.Replace(primerNombre, "[^a-zA-Z0-9]", "");

                ViewBag.ReferenciaPago = $"{sPrefijoTransfer}{primerNombre}{idTransaccion}";
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            return View(modelo);
        }

        /// <summary>
        /// Procesa el comprobante subido por el usuario para una transacción específica.
        /// </summary>
        /// <param name="form">Es el modelo que contiene el archivo y los IDs de pagos seleccionados.</param>
        /// <returns>Redirige a la vista correspondiente según el resultado del proceso.</returns>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarComprobante(SubirComprobanteViewModel form)
        {
            if (form.ArchivoComprobante == null)
            {
                MostrarMensaje("Error", "Debes subir una imagen o PDF.", TipoMensaje.Error);
                int.TryParse(form.IdsPagosSeleccionados.FirstOrDefault(), out int idTrxRet);
                return RedirectToAction("RetomarTransferencia", new { idTransaccion = idTrxRet, token = form.Token });
            }

            int idTransaccion = int.Parse(form.IdsPagosSeleccionados.First());
            int idUserLog = 1;
            if (User.Identity.IsAuthenticated) idUserLog = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. OBTENER ID DEL ARCHIVO VIEJO (PARA BORRARLO LUEGO)
                            int idArchivoViejo = 0;
                            string sqlGetOld = @"SELECT ""Id_Archivo_Comprobante"" FROM ""Eventos_D_Transacciones"" WHERE ""Id_Transaccion"" = @id";
                            using (var cmdOld = new NpgsqlCommand(sqlGetOld, conexion, trans))
                            {
                                cmdOld.Parameters.AddWithValue("@id", idTransaccion);
                                var res = await cmdOld.ExecuteScalarAsync();
                                if (res != DBNull.Value && res != null) idArchivoViejo = (int)res;
                            }

                            // B. GUARDAR ARCHIVO NUEVO
                            byte[] bytes;
                            using (var ms = new MemoryStream()) { await form.ArchivoComprobante.CopyToAsync(ms); bytes = ms.ToArray(); }

                            string sqlFile = @"INSERT INTO ""Rec_Archivos"" 
                        (""Titulo"", ""Descripcion"", ""Tipo"", ""Contenido_Binario"", ""Descargas"", ""Origen"", ""Fecha_Creacion"", ""Id_Usuario_Carga"")
                        VALUES (@tit, 'Comprobante Corrección', @mime, @bin, 0, 'ComprobantesEventos', NOW(), @uid) 
                        RETURNING ""Id_Archivo""";

                            int idArchivoNuevo;
                            using (var cmd = new NpgsqlCommand(sqlFile, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@tit", $"Pago_{idTransaccion}_V2");
                                cmd.Parameters.AddWithValue("@mime", form.ArchivoComprobante.ContentType);
                                cmd.Parameters.AddWithValue("@bin", bytes);
                                cmd.Parameters.AddWithValue("@uid", idUserLog);
                                idArchivoNuevo = (int)await cmd.ExecuteScalarAsync();
                            }

                            // C. ACTUALIZAR TRANSACCIÓN Y LIMPIAR COMENTARIOS
                            string sqlUpd = @"UPDATE ""Eventos_D_Transacciones"" 
                                      SET ""Estatus_Pago"" = 'review', 
                                          ""Id_Archivo_Comprobante"" = @fi, 
                                          ""Fecha_Intento"" = NOW(),
                                          ""Comentarios_Revision"" = NULL 
                                      WHERE ""Id_Transaccion"" = @id";

                            using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@fi", idArchivoNuevo);
                                cmd.Parameters.AddWithValue("@id", idTransaccion);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // D. ELIMINAR EL ARCHIVO VIEJO (LIMPIEZA)
                            // Solo si existía uno antes y es diferente al nuevo (por seguridad)
                            if (idArchivoViejo > 0 && idArchivoViejo != idArchivoNuevo)
                            {
                                string sqlDel = @"DELETE FROM ""Rec_Archivos"" WHERE ""Id_Archivo"" = @old";
                                using (var cmdDel = new NpgsqlCommand(sqlDel, conexion, trans))
                                {
                                    cmdDel.Parameters.AddWithValue("@old", idArchivoViejo);
                                    await cmdDel.ExecuteNonQueryAsync();
                                }
                            }

                            await trans.CommitAsync();

                            // ==========================================================
                            // --- AVISO TRANSFERENCIA EVENTO ---
                            // ==========================================================
                            try
                            {
                                string correosDestino = "";
                                bool alertaActiva = false;

                                // 1. Consultamos la configuración de la alerta en BD
                                string sqlAlerta = @"SELECT ""Correos_Destino"", ""Activo"" FROM ""Sist_EnvioCorreos"" WHERE ""Clave_Evento"" = 'NUEVA_TRANSFERENCIA_EVENTO'";
                                using (var cmdAlerta = new NpgsqlCommand(sqlAlerta, conexion))
                                {
                                    using (var rAlerta = await cmdAlerta.ExecuteReaderAsync())
                                    {
                                        if (await rAlerta.ReadAsync())
                                        {
                                            alertaActiva = (bool)rAlerta["Activo"];
                                            correosDestino = rAlerta["Correos_Destino"].ToString();
                                        }
                                    }
                                }

                                // 2. Si está activa y hay destinatarios, armamos y enviamos el correo
                                if (alertaActiva && !string.IsNullOrWhiteSpace(correosDestino))
                                {
                                    // --- Consultamos los datos en la BD ---
                                    string sTituloEvento = "Evento Desconocido";
                                    decimal TotalEvento = 0;

                                    string sqlDatos = @"SELECT e.""Titulo"", d.""Monto_Total"" 
                            FROM ""Eventos_D_Transacciones"" d
                            JOIN ""Eventos_Catalogo"" e ON d.""Id_Evento"" = e.""Id_Evento""
                            WHERE d.""Id_Transaccion"" = @idTrx";

                                    using (var cmdDatos = new NpgsqlCommand(sqlDatos, conexion))
                                    {
                                        cmdDatos.Parameters.AddWithValue("@idTrx", idTransaccion);
                                        using (var rDatos = await cmdDatos.ExecuteReaderAsync())
                                        {
                                            if (await rDatos.ReadAsync())
                                            {
                                                sTituloEvento = rDatos["Titulo"].ToString();
                                                TotalEvento = (decimal)rDatos["Monto_Total"];
                                            }
                                        }
                                    }

                                    string urlAdmin = Url.Action("Autorizaciones", "Eventos", null, Request.Scheme);

                                    string htmlSoporte = $@"
                                    <div style='font-family: sans-serif; border: 1px solid #eee; padding: 20px; border-radius: 10px;'>
                                        <h2 style='color: #0d6efd;'>🔔 Comprobante de Evento por Validar</h2>
                                        <p>Se ha recibido un nuevo comprobante de transferencia para el evento:</p>
                                        <p><strong>Evento:</strong> {sTituloEvento}</p>
                                        <p><strong>Monto a Validar:</strong> {TotalEvento.ToString("C2")}</p>
                                        <hr style='border: 0; border-top: 1px solid #eee; margin: 20px 0;' />
                                        <p>Por favor, ingresa al panel administrativo para revisar el comprobante y aplicar los pagos a las cuentas correspondientes.</p>
                                        <p align='center' style='margin-top: 25px;'>
                                            <a href='{urlAdmin}' style='background: #ffc107; color: #000; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; display: inline-block;'>
                                                🔍 Revisar Transferencia
                                            </a>
                                        </p>
                                    </div>";

                                    await Funciones.EnviarCorreo(_configuration, correosDestino, $"Comprobante Pendiente - {sTituloEvento}", htmlSoporte);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error al enviar aviso de evento a soporte: {ex.Message}");
                            }

                            MostrarMensaje("Enviado", "Tu Comprobante ha sido enviado a revisión.", TipoMensaje.Exito);

                            if (!string.IsNullOrEmpty(form.Token)) return RedirectToAction("Index", "Home");
                            else return RedirectToAction("MisTransferencias", new { id = 0 });
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("RetomarTransferencia", new { idTransaccion = idTransaccion, token = form.Token });
            }
        }

        /// <summary>
        /// Cancela un intento de pago en estado 'waiting_proof', 'review' o 'rejected'.
        /// </summary>
        /// <param name="id">Es el ID de la transacción a cancelar.</param>
        /// <param name="token">Es un token opcional para identificar la sesión o contexto.</param>
        /// <returns>Es una redirección a la vista correspondiente.</returns>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> CancelarIntento(int id, string token = null)
        {
            if (id <= 0)
            {
                MostrarMensaje("Error", "No se ha podido identificar el intento", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idUserOwner = User.Identity.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;

            // 1. Declarar la variable para la redirección
            string sidRetorno = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 2. SQL: Agregamos t.""Id_Evento"" para saber a qué evento regresar
                    string sqlCheck = @"
                SELECT t.""Id_Usuario"", t.""Id_Cupon"", b.""Token_Pago_Externo"", 
                       r.""Id_Usuario"" as ""Id_Original"", t.""Id_Evento""
                FROM ""Eventos_D_Transacciones"" t
                LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON t.""Id_Transaccion"" = pa.""Id_Transaccion""
                LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                LEFT JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                LEFT JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                WHERE t.""Id_Transaccion"" = @id AND t.""Estatus_Pago"" IN ('waiting_proof', 'review', 'rejected') 
                LIMIT 1";

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int? idCupon = null;
                            bool tienePermiso = false;

                            using (var cmd = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", id);
                                using (var r = await cmd.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        int dbUser = (int)r["Id_Usuario"];
                                        int idOriginal = r["Id_Original"] != DBNull.Value ? (int)r["Id_Original"] : 0;
                                        idCupon = r["Id_Cupon"] as int?;
                                        string dbToken = r["Token_Pago_Externo"]?.ToString();

                                        // 3. Asignar el ID del evento para el retorno
                                        int idEvento = (int)r["Id_Evento"];
                                        sidRetorno = Funciones.EncriptarId(idEvento);

                                        if (dbUser == 0) dbUser = idOriginal;

                                        if (idUserOwner > 0 && dbUser == idUserOwner) tienePermiso = true;
                                        else if (idUserOwner == 0 && !string.IsNullOrEmpty(token) && token == dbToken) tienePermiso = true;
                                    }
                                }
                            }

                            if (!tienePermiso)
                            {
                                MostrarMensaje("Error", "No tienes permiso para cancelar este pago.", TipoMensaje.Alerta);
                                await trans.RollbackAsync();
                                return RedirectToAction("Index");
                            }

                            if (idCupon.HasValue)
                            {
                                await new NpgsqlCommand($"UPDATE \"Sist_Cupones\" SET \"Conteo_Usados\" = GREATEST(\"Conteo_Usados\" - 1, 0) WHERE \"Id_Cupon\" = {idCupon}", conexion, trans).ExecuteNonQueryAsync();
                                if (idUserOwner > 0)
                                    await new NpgsqlCommand($"DELETE FROM \"Sist_Cupones_Uso\" WHERE \"Id_Cupon\" = {idCupon} AND \"Id_Usuario\" = {idUserOwner}", conexion, trans).ExecuteNonQueryAsync();
                            }

                            // 4. Eliminar Transacción
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_E_Pagos_Aplicados\" WHERE \"Id_Transaccion\" = {id}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_D_Transacciones\" WHERE \"Id_Transaccion\" = {id}", conexion, trans).ExecuteNonQueryAsync();

                            // 5. Bitácora
                            string actorIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUserOwner > 0 ? idUserOwner : 1, Modulo, Parametros.AccionesBitacora.Borrar, $"Canceló intento #{id}", actorIp, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Cancelado", "Intento eliminado correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception ex) { await trans.RollbackAsync(); throw ex; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Hubo un problema: " + ex.Message, TipoMensaje.Error);
            }

            return !string.IsNullOrEmpty(token) ? RedirectToAction("Index", "Home") : RedirectToAction("Registro", new { sid = sidRetorno });
        }

        /// <summary>
        /// Descarga el comprobante asociado a un pago.
        /// </summary>
        /// <param name="id">Es el ID del archivo a descargar.</param>
        /// <returns>Retorna el archivo como una descarga o NotFound si no existe.</returns>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> DescargarComprobante(int id, string token = null)
        {
            if (id <= 0) return NotFound();

            bool isAdmin = User.TienePermiso(Modulo, PermisoAdmin) || User.TienePermiso(Modulo, PermisoEditar);
            int idUser = User.Identity.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // ZERO TRUST: Cruzamos el archivo con la transacción para verificar la propiedad
                    string sql = @"
                SELECT r.""Contenido_Binario"", r.""Tipo"", r.""Titulo"" 
                FROM ""Rec_Archivos"" r
                JOIN ""Eventos_D_Transacciones"" t ON r.""Id_Archivo"" = t.""Id_Archivo_Comprobante""
                LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON t.""Id_Transaccion"" = pa.""Id_Transaccion""
                LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                LEFT JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                WHERE r.""Id_Archivo"" = @id 
                AND (
                    @isAdmin = TRUE 
                    OR t.""Id_Usuario"" = @uid 
                    OR b.""Token_Pago_Externo"" = @tok
                ) LIMIT 1";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@isAdmin", isAdmin);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        cmd.Parameters.AddWithValue("@tok", token ?? "");

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                byte[] archivo = (byte[])r["Contenido_Binario"];
                                string mime = r["Tipo"].ToString();
                                string nombre = r["Titulo"].ToString();

                                if (!nombre.EndsWith(".pdf") && !nombre.EndsWith(".jpg") && !nombre.EndsWith(".png"))
                                {
                                    if (mime.Contains("pdf")) nombre += ".pdf";
                                    else if (mime.Contains("png")) nombre += ".png";
                                    else nombre += ".jpg";
                                }
                                return File(archivo, mime, nombre);
                            }
                        }
                    }
                }
            }
            catch { }
            return NotFound("Archivo no encontrado o acceso denegado.");
        }

        /// <summary>
        /// Pantalla de autorizaciones pendientes de revisión por el admin.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> Autorizaciones()
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Error", "No tienes permisos de administrador en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var lista = new List<RevisionPagoViewModel>();
            string Clave_Cuenta_Bancaria = "";
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Añadimos t."External_Reference"
                    string sql = @"
                        SELECT t.""Id_Transaccion"", t.""Monto_Total"", t.""Fecha_Intento"", t.""Id_Archivo_Comprobante"",
                               t.""Monto_Descuento"", t.""Codigo_Cupon_Aplicado"", t.""External_Reference"",
                               u.""NombreCompleto"",
                               e.""Titulo"" as ""Titulo_Evento"", e.""Clave_Cuenta_Bancaria"",
                               
                               (
                                   SELECT json_agg(json_build_object(
                                       'Persona', sub.""Nombre_Completo"", 
                                       'Modalidad', sub.""Modalidad"",
                                       'Concepto', sub.""Nombre_Concepto"", 
                                       'Monto', sub.""Monto_Pagar""
                                   ))
                                   FROM (
                                       SELECT b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"", cal.""Nombre_Concepto"", c.""Monto_Pagar""
                                       FROM ""Eventos_E_Pagos_Aplicados"" pa 
                                       JOIN ""Eventos_C_Cuentas_Cobrar"" c ON pa.""Id_Cuenta"" = c.""Id_Cuenta""
                                       JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                                       JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                                       LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                                       WHERE pa.""Id_Transaccion"" = t.""Id_Transaccion""
                                       ORDER BY b.""Nombre_Completo"" ASC, cal.""Numero_Pago"" ASC
                                   ) as sub
                               ) as ""JsonData""

                        FROM ""Eventos_D_Transacciones"" t
                        LEFT JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario"" = u.""Id_Usuario""
                        JOIN ""Eventos_Catalogo"" e ON t.""Id_Evento"" = e.""Id_Evento""
                        WHERE t.""Estatus_Pago"" = 'review'
                        ORDER BY t.""Fecha_Intento"" ASC";

                    var cuentasBancariasDic = new Dictionary<int, GlobalController.CuentaBancariaInfo>();

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            Clave_Cuenta_Bancaria = r["Clave_Cuenta_Bancaria"] != DBNull.Value ? r["Clave_Cuenta_Bancaria"].ToString() : "";
                            string nombreUser = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Usuario";
                            int idTrxCurrent = (int)r["Id_Transaccion"];

                            var cuentaRow = GlobalController.DatosBancarios.ObtenerCuenta(Clave_Cuenta_Bancaria);
                            cuentasBancariasDic[idTrxCurrent] = cuentaRow;

                            lista.Add(new RevisionPagoViewModel
                            {
                                IdTransaccion = idTrxCurrent,
                                Monto = (decimal)r["Monto_Total"],
                                FechaIntento = (DateTime)r["Fecha_Intento"],
                                IdArchivoComprobante = r["Id_Archivo_Comprobante"] != DBNull.Value ? (int)r["Id_Archivo_Comprobante"] : 0,
                                UsuarioSolicitante = nombreUser,
                                JsonDetalles = r["JsonData"] != DBNull.Value ? r["JsonData"].ToString() : "[]",

                                // Asignación directa desde la base de datos (Única Fuente de la Verdad)
                                ReferenciaEsperada = r["External_Reference"] != DBNull.Value ? r["External_Reference"].ToString() : "NO ASIGNADA",

                                MontoDescuento = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0m,
                                CodigoCupon = r["Codigo_Cupon_Aplicado"] != DBNull.Value ? r["Codigo_Cupon_Aplicado"].ToString() : "",
                                TituloEvento = r["Titulo_Evento"].ToString()
                            });
                        }
                    }
                    ViewBag.CuentasBancariasDic = cuentasBancariasDic;
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cargar la lista de autorizaciones: " + ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        /// <summary>
        /// Procesa el dictamen de un pago por parte del administrador.
        /// </summary>
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DictaminarPago(int idTransaccion, bool aprobado, string comentarios, string folioTransferencia)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Error", "No tienes permisos de administrador en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            // Validación Servidor: Si se aprueba, el Folio es estrictamente necesario.
            if (aprobado && string.IsNullOrWhiteSpace(folioTransferencia))
            {
                MostrarMensaje("Datos Faltantes", "Para aprobar un pago es obligatorio capturar el Folio de rastreabilidad bancaria.", TipoMensaje.Error);
                return RedirectToAction("Autorizaciones");
            }

            try
            {
                string notaAdmin = string.IsNullOrWhiteSpace(comentarios) ? "Sin comentarios adicionales." : comentarios;
                int idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                if (aprobado)
                {
                    // 1. APROBACIÓN (Lógica Contable Principal)
                    // Generamos referencia técnica incluyendo el FOLIO REAL del banco
                    string refAdmin = $"FOLIO_{folioTransferencia.Trim().ToUpper()}";

                    // Esta función marca como 'paid', actualiza Ref_Pasarela y libera los lugares/cuentas
                    await AplicarPagoExitoso(idTransaccion, refAdmin);

                    // 2. GUARDAR EL COMENTARIO EN LA NUEVA COLUMNA
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();
                        string sqlComentario = @"UPDATE ""Eventos_D_Transacciones"" 
                                         SET ""Comentarios_Revision"" = @com 
                                         WHERE ""Id_Transaccion"" = @id";
                        using (var cmd = new NpgsqlCommand(sqlComentario, conexion))
                        {
                            cmd.Parameters.AddWithValue("@com", "APROBADO. Nota: " + notaAdmin);
                            cmd.Parameters.AddWithValue("@id", idTransaccion);
                            await cmd.ExecuteNonQueryAsync();
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar, $"Pago #{idTransaccion} APROBADO manualmente. Folio: {folioTransferencia}", ip);
                        }
                    }

                    MostrarMensaje("Pago Aprobado", "El ingreso se registró correctamente con el folio proporcionado.", TipoMensaje.Exito);
                }
                else
                {
                    // RECHAZO
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();

                        string sql = @"UPDATE ""Eventos_D_Transacciones"" 
                               SET ""Estatus_Pago"" = 'rejected', 
                                   ""Comentarios_Revision"" = @com 
                               WHERE ""Id_Transaccion"" = @id";

                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@com", "RECHAZADO: " + notaAdmin);
                            cmd.Parameters.AddWithValue("@id", idTransaccion);
                            await cmd.ExecuteNonQueryAsync();
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar, $"Pago #{idTransaccion} RECHAZADO manualmente.", ip);
                        }
                    }
                    MostrarMensaje("Rechazado", "La solicitud fue rechazada. El usuario verá el motivo.", TipoMensaje.Alerta);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo procesar el dictamen: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Autorizaciones");
        }

        /// <summary>
        /// Muestra la imagen o PDF del comprobante de un pago.
        /// </summary>
        /// <param name="id">Es el ID del archivo a mostrar.</param>
        /// <returns>Retorna el archivo como una vista o NotFound si no existe.</returns>
        [Authorize]
        public async Task<IActionResult> VerComprobanteImagen(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return NotFound();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Contenido_Binario"", ""Tipo"" FROM ""Rec_Archivos"" WHERE ""Id_Archivo"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync() && r["Contenido_Binario"] != DBNull.Value)
                            {
                                return File((byte[])r["Contenido_Binario"], r["Tipo"].ToString());
                            }
                        }
                    }
                }
            }
            catch { }
            return NotFound();
        }

        /// <summary>
        /// Muestra el Dashboard con estadísticas financieras, de ocupación, género y modalidades.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> PanelControl(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en eventos", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            bool bPermisoRegistroGratuito = User.TienePermiso(Modulo, PermisoAdmin);

            // VALIDACIÓN COMPUESTA EN EL SERVIDOR: Debe ser admin o editor en Eventos Y en Caja
            bool bPermisoCobroEfectivo = (User.TienePermiso(Parametros.Modulos.Eventos, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Eventos, PermisoEditar))
                                      && (User.TienePermiso(Parametros.Modulos.Caja, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Caja, PermisoEditar));

            ViewBag.bPermisoCobroEfectivo = bPermisoCobroEfectivo;
            DashboardEventoViewModel modelo = new DashboardEventoViewModel();
            var cultura = new System.Globalization.CultureInfo("es-MX");

            try
            {
                var id = Funciones.DesencriptarId(sid);
                modelo = new DashboardEventoViewModel { IdEvento = id };

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. DATOS GENERALES DEL EVENTO
                    string sqlEvento = @"SELECT ""Titulo"", ""Fecha_Inicio"", ""Cupo_Maximo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlEvento, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.IdEventoEncriptado = Funciones.EncriptarId(id);
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Fecha = (DateTime)r["Fecha_Inicio"];
                                ViewBag.Cupo = (int)r["Cupo_Maximo"];
                            }
                            else
                            {
                                MostrarMensaje("Error", "No se ha encontrado el evento", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // 2. ESTADÍSTICAS FINANCIERAS Y GLOBALES (AHORA CON DIVISIONES POR MÉTODO)
                    string sqlFinanzas = @"
            SELECT 
                -- TOTAL INSCRITOS REALES: Solo cuenta a los asistentes que ya tienen al menos 1 pago aprobado
                COUNT(DISTINCT CASE 
                    WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND cx.""Pagado"" = TRUE) 
                    THEN b.""Id_Asistente"" 
                END) as ""TotalInscritos"",

                -- RECAUDADO GLOBAL
                (SELECT COALESCE(SUM(""Total_Neto""), 0) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Completado"" = TRUE) as ""Recaudado"",        
                
                -- RECAUDADO POR TARJETA (Stripe cs_ o pi_)
                (SELECT COALESCE(SUM(""Total_Neto""), 0) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Completado"" = TRUE AND (""Ref_Pasarela"" LIKE 'cs_%' OR ""Ref_Pasarela"" LIKE 'pi_%')) as ""RecaudadoTarjeta"",        
                
                -- RECAUDADO POR TRANSFERENCIA
                (SELECT COALESCE(SUM(""Total_Neto""), 0) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Completado"" = TRUE AND (""Ref_Pasarela"" NOT LIKE 'cs_%' AND ""Ref_Pasarela"" NOT LIKE 'pi_%' OR ""Ref_Pasarela"" IS NULL)) as ""RecaudadoTransferencia"",        

                -- Descuentos: Suma de lo que perdonamos
                (SELECT COALESCE(SUM(""Monto_Descuento""), 0) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Completado"" = TRUE) as ""Descuentos"",

                -- PENDIENTE FIRME: Suma la deuda SOLO de aquellos asistentes que YA tienen al menos 1 pago aprobado
                COALESCE(SUM(CASE 
                    WHEN c.""Pagado"" = FALSE 
                    AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND cx.""Pagado"" = TRUE) 
                    THEN c.""Monto_Pagar"" 
                    ELSE 0 
                END), 0) as ""PendienteFirme"",

                COUNT(DISTINCT CASE WHEN b.""Es_Pagado"" = TRUE THEN b.""Id_Asistente"" END) as ""TotalPagados""
            FROM ""Eventos_B_Asistentes"" b
            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
            LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON b.""Id_Asistente"" = c.""Id_Asistente""
            WHERE r.""Id_Evento"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlFinanzas, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.TotalInscritos = Convert.ToInt32(r["TotalInscritos"]);
                                modelo.DineroRecaudado = (decimal)r["Recaudado"];

                                // Extraemos los nuevos valores a ViewBag para no alterar tu ViewModel original
                                ViewBag.DineroTarjeta = (decimal)r["RecaudadoTarjeta"];
                                ViewBag.DineroTransferencia = (decimal)r["RecaudadoTransferencia"];

                                modelo.TotalDescuentosOtorgados = (decimal)r["Descuentos"];
                                modelo.DineroPendiente = (decimal)r["PendienteFirme"];
                                modelo.TotalPagados = Convert.ToInt32(r["TotalPagados"]);
                            }
                        }
                    }

                    modelo.ProyeccionTotal = modelo.DineroRecaudado + modelo.DineroPendiente;
                    if (ViewBag.Cupo > 0)
                    {
                        modelo.PorcentajeOcupacion = (int)Math.Round((double)modelo.TotalPagados / (int)ViewBag.Cupo * 100);
                    }

                    // 3. GRÁFICA: CRONOLOGÍA (Inscritos por día)
                    string sqlCrono = @"
            SELECT TO_CHAR(r.""Fecha_Registro"", 'YYYY-MM-DD') as ""Fecha"", COUNT(*) as ""Cantidad""
            FROM ""Eventos_B_Asistentes"" b
            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
            WHERE r.""Id_Evento"" = @id
            GROUP BY TO_CHAR(r.""Fecha_Registro"", 'YYYY-MM-DD')
            ORDER BY ""Fecha"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlCrono, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.CronologiaRegistros.Add(new DatoGrafica
                                {
                                    Etiqueta = DateTime.Parse(r["Fecha"].ToString()).ToString("dd MMM"),
                                    Valor = Convert.ToInt32(r["Cantidad"])
                                });
                            }
                        }
                    }

                    // 4. GRÁFICA: GÉNERO
                    string sqlGen = @"
            SELECT ""Genero"", COUNT(*) as ""Total""
            FROM ""Eventos_B_Asistentes"" b
            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
            WHERE r.""Id_Evento"" = @id
            GROUP BY ""Genero""";

                    using (var cmd = new NpgsqlCommand(sqlGen, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string g = r["Genero"]?.ToString();
                                string etiqueta = g == "H" ? "Hombres" : (g == "M" ? "Mujeres" : "No especificado");
                                modelo.Generos.Add(new DatoGrafica { Etiqueta = etiqueta, Valor = Convert.ToInt32(r["Total"]) });
                            }
                        }
                    }

                    // 5. GRÁFICA DE MODALIDADES (SUBTIPOS)
                    string sqlSub = @"
            SELECT COALESCE(s.""Nombre"", 'General') as ""Modalidad"", COUNT(*) as ""Total""
            FROM ""Eventos_B_Asistentes"" b
            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
            LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
            WHERE r.""Id_Evento"" = @id
            GROUP BY s.""Nombre""
            ORDER BY ""Total"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlSub, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.SubtiposStats.Add(new DatoGrafica
                                {
                                    Etiqueta = r["Modalidad"].ToString(),
                                    Valor = Convert.ToInt32(r["Total"])
                                });
                            }
                        }
                    }

                    // 6. ÚLTIMOS INSCRITOS
                    string sqlLast = @"
            SELECT b.""Nombre_Completo"", r.""Fecha_Registro"", 
                   CASE WHEN b.""Es_Pagado"" THEN 'Pagado' ELSE 'Pendiente' END as ""Estado""
            FROM ""Eventos_B_Asistentes"" b
            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
            WHERE r.""Id_Evento"" = @id
            ORDER BY r.""Fecha_Registro"" DESC LIMIT 10";

                    using (var cmd = new NpgsqlCommand(sqlLast, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.UltimosInscritos.Add(new InscritoReciente
                                {
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    FechaRegistro = (DateTime)r["Fecha_Registro"],
                                    EstadoPago = r["Estado"].ToString()
                                });
                            }
                        }
                    }

                    // 7. RESPUESTAS FRECUENTES
                    string sqlRespuestas = @"
            SELECT p.""Texto_Pregunta"", r.""Valor_Respuesta"", COUNT(*) as ""Frecuencia""
            FROM ""Eventos_Preguntas"" p
            JOIN ""Eventos_Respuestas"" r ON p.""Id_Pregunta"" = r.""Id_Pregunta""
            WHERE p.""Id_Evento"" = @id
            GROUP BY p.""Texto_Pregunta"", r.""Valor_Respuesta""
            ORDER BY p.""Texto_Pregunta"", ""Frecuencia"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlRespuestas, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            string preguntaActual = "";
                            var estadisticaActual = new EstadisticaPregunta();

                            while (await r.ReadAsync())
                            {
                                string preg = r["Texto_Pregunta"].ToString();
                                string resp = r["Valor_Respuesta"].ToString();
                                int count = Convert.ToInt32(r["Frecuencia"]);

                                if (preg != preguntaActual)
                                {
                                    if (estadisticaActual.Respuestas.Any())
                                    {
                                        modelo.PreguntasFrecuentes.Add(estadisticaActual);
                                    }
                                    estadisticaActual = new EstadisticaPregunta { Pregunta = preg };
                                    preguntaActual = preg;
                                }

                                if (!string.IsNullOrWhiteSpace(resp))
                                {
                                    estadisticaActual.Respuestas.Add(new DatoGrafica
                                    {
                                        Etiqueta = resp,
                                        Valor = count
                                    });
                                }
                            }
                            if (estadisticaActual.Respuestas.Any()) modelo.PreguntasFrecuentes.Add(estadisticaActual);
                        }
                    }

                    // 8. OBTENER LOS GASTOS VINCULADOS AL EVENTO DESDE LA CAJA
                    string sqlGastos = @"
            SELECT c.""Concepto"", c.""Monto"", c.""Fecha"", u.""NombreCompleto""
            FROM ""Fin_Caja"" c
            LEFT JOIN ""Sist_Usuarios"" u ON c.""Id_Usuario"" = u.""Id_Usuario""
            WHERE c.""Id_Evento"" = @id AND c.""Tipo"" = 'Egreso'
            ORDER BY c.""Fecha"" DESC";

                    decimal totalGastos = 0;
                    var listaGastos = new List<dynamic>();

                    using (var cmdGastos = new NpgsqlCommand(sqlGastos, conexion))
                    {
                        cmdGastos.Parameters.AddWithValue("@id", id);
                        using (var rG = await cmdGastos.ExecuteReaderAsync())
                        {
                            while (await rG.ReadAsync())
                            {
                                decimal montoGasto = (decimal)rG["Monto"];
                                totalGastos += montoGasto;

                                dynamic gasto = new System.Dynamic.ExpandoObject();
                                gasto.Concepto = rG["Concepto"].ToString();
                                gasto.Monto = montoGasto;
                                gasto.Fecha = (DateTime)rG["Fecha"];
                                gasto.Usuario = rG["NombreCompleto"]?.ToString() ?? "Desconocido";

                                listaGastos.Add(gasto);
                            }
                        }
                    }

                    ViewBag.TotalGastos = totalGastos;
                    ViewBag.DetalleGastos = listaGastos;
                    ViewBag.UtilidadNeta = modelo.DineroRecaudado - totalGastos;
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.bPermisoRegistroGratuito = bPermisoRegistroGratuito;
            return View(modelo);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarInscripcionAbandonada(int idAsistente, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Forbid();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int idEvento = Funciones.DesencriptarId(sid);
                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "El evento está bloqueado por logística. No se pueden eliminar inscripciones.", TipoMensaje.Alerta);
                        return RedirectToAction("Inscritos", new { sid = sid });
                    }
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. RE-VALIDACIÓN DE SEGURIDAD EXTREMA (Intentos y Pagos Reales)
                            string sqlCheck = @"
                        SELECT 
                            (SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
                             JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion"" = e.""Id_Transaccion"" 
                             JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta"" 
                             WHERE c.""Id_Asistente"" = @idA) as ""TotalIntentos"",
                             
                            (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" 
                             WHERE ""Id_Asistente"" = @idA AND ""Pagado"" = TRUE) as ""TotalPagados"",
                             
                            (SELECT ""Es_Pagado"" FROM ""Eventos_B_Asistentes"" 
                             WHERE ""Id_Asistente"" = @idA) as ""EsPagadoGlobal""";

                            using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmdCheck.Parameters.AddWithValue("@idA", idAsistente);
                                using (var reader = await cmdCheck.ExecuteReaderAsync())
                                {
                                    if (await reader.ReadAsync())
                                    {
                                        long intentos = Convert.ToInt64(reader["TotalIntentos"]);
                                        long pagados = Convert.ToInt64(reader["TotalPagados"]);
                                        bool esPagadoGlobal = reader["EsPagadoGlobal"] != DBNull.Value && (bool)reader["EsPagadoGlobal"];

                                        // Bloqueo 1: Tiene un intento de pago en pasarela
                                        if (intentos > 0)
                                        {
                                            throw new Exception("No se puede eliminar: Se detectó un intento de pago reciente en pasarela.");
                                        }

                                        // Bloqueo 2: Está liquidado en efectivo o cortesía (lo que acabamos de agregar)
                                        if (pagados > 0 || esPagadoGlobal)
                                        {
                                            throw new Exception("Seguridad: No se puede eliminar a un asistente que ya cuenta con pagos liquidados o cortesías.");
                                        }
                                    }
                                    else
                                    {
                                        throw new Exception("El registro no existe.");
                                    }
                                }
                            }

                            // 2. OBTENER ID_REGISTRO PARA LIMPIEZA POSTERIOR
                            int idRegistro = 0;
                            using (var cmdReg = new NpgsqlCommand(@"SELECT ""Id_Registro"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = @idA", conexion, trans))
                            {
                                cmdReg.Parameters.AddWithValue("@idA", idAsistente);
                                idRegistro = Convert.ToInt32(await cmdReg.ExecuteScalarAsync());
                            }

                            // 3. BORRADO EN CASCADA MANUAL
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            string sqlDelEncAbandonada = @"
                                DELETE FROM ""Encuestas_Respuestas_Detalle"" 
                                WHERE ""Id_Respuesta"" IN (SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" WHERE ""Id_Asistente_Evento"" = @idA);
                                DELETE FROM ""Encuestas_Respuestas_Header"" 
                                WHERE ""Id_Asistente_Evento"" = @idA;";
                            using (var cmdDelEnc = new NpgsqlCommand(sqlDelEncAbandonada, conexion, trans))
                            {
                                cmdDelEnc.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelEnc.ExecuteNonQueryAsync();
                            }
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_I_Alojamiento_Asignaciones\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_J_Prioridad_Alojamiento\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Asistentes_ProductosExtra\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_B_Asistentes\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // 4. LIMPIEZA DE REGISTRO PADRE (Si ya no quedan asistentes)
                            long restantes = (long)await new NpgsqlCommand($"SELECT COUNT(*) FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\" = {idRegistro}", conexion, trans).ExecuteScalarAsync();
                            if (restantes == 0)
                            {
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_A_Registros\" WHERE \"Id_Registro\" = {idRegistro}", conexion, trans).ExecuteNonQueryAsync();
                            }

                            // 5. BITÁCORA
                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó inscripción fantasma ID #{idAsistente}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Eliminado", "La inscripción fantasma fue removida y el cupo se liberó.", TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw ex;
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Inscritos", new { sid = sid });
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Inscritos(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new GestionInscritosViewModel();
            try
            {
                int idEvento = Funciones.DesencriptarId(sid);
                modelo.IdEvento = idEvento;
                modelo.IdEventoEncriptado = sid;

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int idEncReqInscritos = 0;
                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"", ""Id_Encuesta_Requisito"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        using (var rEv = await cmdEv.ExecuteReaderAsync())
                        {
                            if (await rEv.ReadAsync())
                            {
                                modelo.TituloEvento = rEv["Titulo"].ToString();
                                if (rEv["Id_Encuesta_Requisito"] != DBNull.Value) idEncReqInscritos = Convert.ToInt32(rEv["Id_Encuesta_Requisito"]);
                            }
                        }
                    }
                    ViewBag.TieneEncuestaRequisito = idEncReqInscritos > 0;

                    var todasModalidades = new List<SubtipoEventoItem>();
                    string sqlCat = @"SELECT ""Id_Subtipo"", ""Nombre"", ""Costo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento"" = @id AND ""Activo"" = TRUE ORDER BY ""Costo"" ASC, ""Nombre"" ASC";
                    using (var cmdCat = new NpgsqlCommand(sqlCat, conexion))
                    {
                        cmdCat.Parameters.AddWithValue("@id", idEvento);
                        using (var rCat = await cmdCat.ExecuteReaderAsync())
                        {
                            while (await rCat.ReadAsync())
                            {
                                todasModalidades.Add(new SubtipoEventoItem { Id_Subtipo = (int)rCat["Id_Subtipo"], Nombre = rCat["Nombre"].ToString(), Costo = (decimal)rCat["Costo"] });
                            }
                        }
                    }
                    ViewBag.TodasModalidades = todasModalidades;

                    string sql = @"
                    SELECT 
                        COALESCE(s.""Id_Subtipo"", 0) as ""IdSubtipo"",
                        COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                        b.""Id_Asistente"", b.""Nombre_Completo"", b.""Genero"", b.""Edad"", b.""Es_Pagado"" as ""EsPagadoGlobal"", b.""Token_Pago_Externo"" as ""TokenExterno"",
                        COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                                  WHERE h.""Id_Encuesta"" = @idEncReq 
                                    AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida"",
                        r.""Fecha_Registro"", c.""Id_Cuenta"", c.""Monto_Pagar"", c.""Pagado"", c.""Fecha_Pagado"",
                        CASE 
                            WHEN cal.""Numero_Pago"" <= -10000 THEN 
                                cal.""Nombre_Concepto"" || 
                                CASE WHEN (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = c.""Id_Asistente"" AND c2.""Id_Calendario"" = c.""Id_Calendario"") > 1 THEN
                                    ' (Unidad ' || (
                                        SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c_sub 
                                        WHERE c_sub.""Id_Asistente"" = c.""Id_Asistente"" 
                                          AND c_sub.""Id_Calendario"" = c.""Id_Calendario"" 
                                          AND c_sub.""Id_Cuenta"" <= c.""Id_Cuenta""
                                    ) || ' de ' || (
                                        SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c_tot 
                                        WHERE c_tot.""Id_Asistente"" = c.""Id_Asistente"" 
                                          AND c_tot.""Id_Calendario"" = c.""Id_Calendario""
                                    ) || ')'
                                ELSE '' END
                            ELSE cal.""Nombre_Concepto"" 
                        END AS ""Nombre_Concepto"",
                        cal.""Numero_Pago"",
                        (SELECT t.""Ref_Pasarela"" 
                         FROM ""Eventos_E_Pagos_Aplicados"" pa 
                         JOIN ""Eventos_D_Transacciones"" t ON pa.""Id_Transaccion"" = t.""Id_Transaccion"" 
                         WHERE pa.""Id_Cuenta"" = c.""Id_Cuenta"" 
                         ORDER BY t.""Completado"" DESC, t.""Id_Transaccion"" DESC 
                         LIMIT 1) as ""Ref_Pasarela"",
                        COALESCE(j.""Puntaje"", 0) as ""Score"",
                        COALESCE(s.""Costo"", (SELECT ""Costo_Entrada"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id)) as ""CostoOficial"",
                        (SELECT SUM(""Monto_Pagar"") FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = b.""Id_Asistente"") as ""TotalCuentas"",
                        (SELECT COUNT(*) FROM ""Eventos_E_Pagos_Aplicados"" pa2 JOIN ""Eventos_D_Transacciones"" t2 ON pa2.""Id_Transaccion"" = t2.""Id_Transaccion"" WHERE pa2.""Id_Cuenta"" IN (SELECT ""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = b.""Id_Asistente"")) as ""TotalIntentos"",
                        (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c3 JOIN ""Eventos_Calendario"" cal3 ON c3.""Id_Calendario"" = cal3.""Id_Calendario"" WHERE c3.""Id_Asistente"" = b.""Id_Asistente"" AND cal3.""Numero_Pago"" = -1) as ""CantidadAjustes""
                    FROM ""Eventos_B_Asistentes"" b
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                    LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                    LEFT JOIN ""Eventos_J_Prioridad_Alojamiento"" j ON b.""Id_Asistente"" = j.""Id_Asistente""
                    JOIN ""Eventos_C_Cuentas_Cobrar"" c ON b.""Id_Asistente"" = c.""Id_Asistente""
                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                    WHERE r.""Id_Evento"" = @id
                    ORDER BY j.""Puntaje"" DESC, r.""Fecha_Registro"" ASC, b.""Id_Asistente"" ASC, 
                             CASE WHEN cal.""Numero_Pago"" > 0 THEN 1 WHEN cal.""Numero_Pago"" = -1 THEN 2 ELSE 3 END, 
                             cal.""Numero_Pago"" ASC, c.""Id_Cuenta"" ASC;";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        cmd.Parameters.AddWithValue("@idEncReq", idEncReqInscritos);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idSub = (int)r["IdSubtipo"];
                                int idAsis = (int)r["Id_Asistente"];
                                var grupo = modelo.Grupos.FirstOrDefault(g => g.IdSubtipo == idSub);
                                if (grupo == null) { grupo = new GrupoModalidad { IdSubtipo = idSub, NombreModalidad = r["Modalidad"].ToString() }; modelo.Grupos.Add(grupo); }

                                var asistente = grupo.Asistentes.FirstOrDefault(a => a.IdAsistente == idAsis);
                                if (asistente == null)
                                {
                                    asistente = new AsistenteGestion
                                    {
                                        IdAsistente = idAsis,
                                        TokenExterno = r["TokenExterno"]?.ToString() ?? "",
                                        NombreCompleto = r["Nombre_Completo"].ToString(),
                                        Edad = Convert.ToInt32(r["Edad"]),
                                        Genero = r["Genero"].ToString(),
                                        Score = Convert.ToDecimal(r["Score"]),
                                        CostoOficialPaquete = Convert.ToDecimal(r["CostoOficial"]),
                                        TotalCuentasAsignadas = r["TotalCuentas"] != DBNull.Value ? Convert.ToDecimal(r["TotalCuentas"]) : 0,
                                        TieneAjusteFinanciero = Convert.ToInt32(r["CantidadAjustes"]) > 0, // <--- DETECTA TRASPASO TIPO 2
                                        EsPagadoGlobal = (bool)r["EsPagadoGlobal"],
                                        EsAbandonado = Convert.ToInt32(r["TotalIntentos"]) == 0,
                                        EncuestaRespondida = idEncReqInscritos > 0 ? (bool)r["EncuestaRespondida"] : true
                                    };
                                    grupo.Asistentes.Add(asistente);
                                }
                                asistente.Pagos.Add(new PagoGestion { IdCuenta = (int)r["Id_Cuenta"], Concepto = r["Nombre_Concepto"].ToString() ?? $"Pago #{r["Numero_Pago"]}", Monto = (decimal)r["Monto_Pagar"], Pagado = (bool)r["Pagado"], FechaPago = r["Fecha_Pagado"] as DateTime?, Referencia = r["Ref_Pasarela"]?.ToString() });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditarDatosBasicos(string sid, int idAsistente, string nuevoNombre, int nuevaEdad, string nuevoGenero)
        {
            // Validación de permisos (asegúrate de que los permisos correspondan a los de tu panel)
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                return Forbid();
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int idEvento = Funciones.DesencriptarId(sid);

                    // Validar que el evento no esté bloqueado
                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "Por logística, el evento se encuentra bloqueado.", TipoMensaje.Error);
                        return RedirectToAction("Inscritos", new { sid = sid });
                    }

                    // Ejecutar el Update de los tres campos
                    string sql = @"UPDATE ""Eventos_B_Asistentes"" 
                           SET ""Nombre_Completo"" = @nom, 
                               ""Edad"" = @edad, 
                               ""Genero"" = @gen 
                           WHERE ""Id_Asistente"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nom", nuevoNombre.Trim());
                        cmd.Parameters.AddWithValue("@edad", nuevaEdad);
                        cmd.Parameters.AddWithValue("@gen", nuevoGenero);
                        cmd.Parameters.AddWithValue("@id", idAsistente);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Bitácora de la acción
                    int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                        Parametros.AccionesBitacora.Editar,
                        $"Editó datos básicos (Nombre, Edad, Género) del asistente ID {idAsistente}",
                        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1");

                    MostrarMensaje("Actualizado", "Los datos se actualizaron correctamente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Inscritos", new { sid = sid });
        }

        /// <summary>
        /// Núcleo financiero: Prepara la anulación en base de datos, ejecuta el refund en Stripe, 
        /// y si Stripe es exitoso, consolida la transacción local.
        /// </summary>
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AnularPagoIndividual(int idCuenta, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Denegado", "Solo los administradores pueden hacer devoluciones financieras.", TipoMensaje.Error);
                return RedirectToAction("Inscritos", new { sid = sid });
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. OBTENER INFORMACIÓN EXACTA DE ESE PAGO
                            string sqlInfo = @"
                                SELECT c.""Monto_Pagar"", c.""Id_Asistente"",
                                       t.""Ref_Pasarela"", t.""Id_Transaccion""
                                FROM ""Eventos_C_Cuentas_Cobrar"" c
                                JOIN ""Eventos_E_Pagos_Aplicados"" e ON c.""Id_Cuenta"" = e.""Id_Cuenta""
                                JOIN ""Eventos_D_Transacciones"" t ON e.""Id_Transaccion"" = t.""Id_Transaccion""
                                WHERE c.""Id_Cuenta"" = @idC AND c.""Pagado"" = TRUE 
                                FOR UPDATE OF c LIMIT 1";

                            decimal montoAReembolsar = 0;
                            int idAsistente = 0;
                            int idTransaccion = 0;
                            string refStripe = "";

                            using (var cmdInfo = new NpgsqlCommand(sqlInfo, conexion, trans))
                            {
                                cmdInfo.Parameters.AddWithValue("@idC", idCuenta);
                                using (var r = await cmdInfo.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        montoAReembolsar = (decimal)r["Monto_Pagar"];
                                        idAsistente = (int)r["Id_Asistente"];
                                        idTransaccion = (int)r["Id_Transaccion"];
                                        refStripe = r["Ref_Pasarela"]?.ToString() ?? "";
                                    }
                                    else
                                    {
                                        // Si un admin llega milisegundos tarde, choca con el candado y termina aquí limpiamente
                                        await trans.RollbackAsync();
                                        MostrarMensaje("Error", "No se encontró el pago o ya estaba anulado por otro administrador.", TipoMensaje.Alerta);
                                        return RedirectToAction("Inscritos", new { sid = sid });
                                    }
                                }
                            }

                            // 2. INICIAR LA TRANSACCIÓN GLOBAL (BD + Pasarela)
                            // A. Desvincular de la tabla puente (Tabla E)
                            string sqlDelE = @"DELETE FROM ""Eventos_E_Pagos_Aplicados"" WHERE ""Id_Cuenta"" = @idC AND ""Id_Transaccion"" = @idT";
                            using (var cmdDelE = new NpgsqlCommand(sqlDelE, conexion, trans))
                            {
                                cmdDelE.Parameters.AddWithValue("@idC", idCuenta);
                                cmdDelE.Parameters.AddWithValue("@idT", idTransaccion);
                                await cmdDelE.ExecuteNonQueryAsync();
                            }

                            // B. Regresar la cuenta a "No Pagado"
                            string sqlUpdC = @"UPDATE ""Eventos_C_Cuentas_Cobrar"" SET ""Pagado"" = FALSE, ""Fecha_Pagado"" = NULL WHERE ""Id_Cuenta"" = @idC";
                            using (var cmdUpdC = new NpgsqlCommand(sqlUpdC, conexion, trans))
                            {
                                cmdUpdC.Parameters.AddWithValue("@idC", idCuenta);
                                await cmdUpdC.ExecuteNonQueryAsync();
                            }

                            // C. Asegurar que el asistente pierda el "Es_Pagado" SOLO SI ya no tiene otras cuentas pagadas
                            string sqlUpdB = @"
                        UPDATE ""Eventos_B_Asistentes"" 
                        SET ""Es_Pagado"" = FALSE 
                        WHERE ""Id_Asistente"" = @idA 
                        AND NOT EXISTS (
                            SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" 
                            WHERE ""Id_Asistente"" = @idA AND ""Pagado"" = TRUE
                        )";
                            using (var cmdUpdB = new NpgsqlCommand(sqlUpdB, conexion, trans))
                            {
                                cmdUpdB.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdUpdB.ExecuteNonQueryAsync();
                            }

                            // D. Restar el monto reembolsado de la transacción principal para cuadrar la contabilidad
                            string sqlUpdD = @"UPDATE ""Eventos_D_Transacciones"" SET ""Total_Neto"" = ""Total_Neto"" - @monto WHERE ""Id_Transaccion"" = @idT";
                            using (var cmdUpdD = new NpgsqlCommand(sqlUpdD, conexion, trans))
                            {
                                cmdUpdD.Parameters.AddWithValue("@idT", idTransaccion);
                                cmdUpdD.Parameters.AddWithValue("@monto", montoAReembolsar);
                                await cmdUpdD.ExecuteNonQueryAsync();
                            }

                            // 3. LLAMAR A STRIPE (El punto de no retorno)
                            string notaBitacora = "";
                            bool esPagoStripe = refStripe.StartsWith("pi_") || refStripe.StartsWith("ch_");

                            if (esPagoStripe)
                            {
                                var options = new Stripe.RefundCreateOptions
                                {
                                    PaymentIntent = refStripe.StartsWith("pi_") ? refStripe : null,
                                    Charge = refStripe.StartsWith("ch_") ? refStripe : null,
                                    Amount = (long)(montoAReembolsar * 100),
                                    Reason = Stripe.RefundReasons.RequestedByCustomer
                                };
                                var service = new Stripe.RefundService();
                                var refund = await service.CreateAsync(options);

                                if (refund.Status == "succeeded" || refund.Status == "pending")
                                {
                                    notaBitacora = $"Stripe Refund OK (Refund ID: {refund.Id})";
                                }
                                else
                                {
                                    throw new Exception($"Stripe devolvió estatus no esperado: {refund.Status}");
                                }
                            }
                            else
                            {
                                notaBitacora = "Anulación de pago manual/transferencia.";
                            }

                            int idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar,
                                $"Anuló y reembolsó pago de cuenta #{idCuenta} por ${montoAReembolsar}. Info: {notaBitacora}", ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Reembolso Exitoso", $"El pago por ${montoAReembolsar} fue anulado y reembolsado correctamente.", TipoMensaje.Exito);
                        }
                        catch (Stripe.StripeException e)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Error en Stripe", $"No se pudo reembolsar el dinero: {e.StripeError.Message}", TipoMensaje.Error);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Error Interno", $"Se abortó la anulación por seguridad: {ex.Message}", TipoMensaje.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Crítico", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Inscritos", new { sid = sid });
        }

        /// <summary>
        /// Descarga un paquete ZIP únicamente con las fotos y PDFs de los comprobantes del evento.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> DescargarComprobantesZip(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos para descargar los comprobantes.", TipoMensaje.Alerta);
                return RedirectToAction("PanelControl", new { sid = sid });
            }

            int idEvento = Funciones.DesencriptarId(sid);
            string tituloEvento = "Comprobantes";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        var resEv = await cmdEv.ExecuteScalarAsync();
                        if (resEv != null) tituloEvento = resEv.ToString().Replace(" ", "_");
                    }

                    using (var outStream = new MemoryStream())
                    {
                        using (var archive = new System.IO.Compression.ZipArchive(outStream, System.IO.Compression.ZipArchiveMode.Create, true))
                        {
                            // Buscamos transacciones que tengan archivo físico
                            string sql = @"
                        SELECT t.""External_Reference"", u.""NombreCompleto"", r.""Contenido_Binario"", r.""Tipo"", r.""Titulo""
                        FROM ""Eventos_D_Transacciones"" t
                        JOIN ""Rec_Archivos"" r ON t.""Id_Archivo_Comprobante"" = r.""Id_Archivo""
                        LEFT JOIN ""Sist_Usuarios"" u ON t.""Id_Usuario"" = u.""Id_Usuario""
                        WHERE t.""Id_Evento"" = @id";

                            using (var cmd = new NpgsqlCommand(sql, conexion))
                            {
                                cmd.Parameters.AddWithValue("@id", idEvento);
                                using (var reader = await cmd.ExecuteReaderAsync())
                                {
                                    while (await reader.ReadAsync())
                                    {
                                        byte[] bytes = (byte[])reader["Contenido_Binario"];
                                        string ext = reader["Tipo"].ToString().Contains("pdf") ? ".pdf" : ".jpg";
                                        string nombreLimpio = System.Text.RegularExpressions.Regex.Replace(reader["NombreCompleto"]?.ToString() ?? "Anonimo", "[^a-zA-Z0-9]", "");
                                        string nombreArchivo = $"{reader["External_Reference"]}_{nombreLimpio}{ext}";

                                        var entry = archive.CreateEntry(nombreArchivo, System.IO.Compression.CompressionLevel.Fastest);
                                        using (var entryStream = entry.Open()) await entryStream.WriteAsync(bytes, 0, bytes.Length);
                                    }
                                }
                            }
                        }
                        return File(outStream.ToArray(), "application/zip", $"Fotos_Pagos_{tituloEvento}.zip");
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("PanelControl", new { sid = sid }); }
        }

        /// <summary>
        /// Genera el Mega Reporte Excel con múltiples hojas (Totales, Asistentes, Ingresos, Deudas y Habitaciones).
        /// Diseñado para ser la fuente de verdad contable y logística del evento.
        /// </summary>
        [Authorize]
        public async Task<IActionResult> DescargarReporteEvento(string sid)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Admin) && !User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                var id = Funciones.DesencriptarId(sid);
                string tituloEvento = "Reporte";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // OBTENER TÍTULO PARA EL NOMBRE DEL ARCHIVO
                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento""=@id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", id);
                        var resEv = await cmdEv.ExecuteScalarAsync();
                        if (resEv != null) tituloEvento = resEv.ToString().Replace(" ", "_");
                    }

                    string tituloOriginal = "Reporte";
                    string tituloEventoZip = "Reporte";
                    DateTime fechaEvento = DateTime.Now;
                    bool eventoActivo = false;

                    // OBTENER DATOS TÉCNICOS DEL EVENTO
                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"", ""Fecha_Inicio"", ""Activo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento""=@id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", id);
                        using (var readerEv = await cmdEv.ExecuteReaderAsync())
                        {
                            if (await readerEv.ReadAsync())
                            {
                                tituloOriginal = readerEv["Titulo"].ToString();
                                tituloEventoZip = tituloOriginal.Replace(" ", "_");
                                fechaEvento = (DateTime)readerEv["Fecha_Inicio"];
                                eventoActivo = (bool)readerEv["Activo"];
                            }
                        }
                    }

                    // OBTENER DEFINICIONES DE PREGUNTAS
                    var preguntasEvento = new List<dynamic>();
                    using (var cmdP = new NpgsqlCommand(@"SELECT ""Id_Pregunta"", ""Texto_Pregunta"" FROM ""Eventos_Preguntas"" WHERE ""Id_Evento""=@id ORDER BY ""Id_Pregunta"" ASC", conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync()) preguntasEvento.Add(new { Id = (int)r["Id_Pregunta"], Texto = r["Texto_Pregunta"].ToString() });
                        }
                    }

                    // OBTENER RESPUESTAS EN MEMORIA
                    var diccionarioRespuestas = new Dictionary<int, Dictionary<int, string>>();
                    string sqlResp = @"SELECT r.""Id_Asistente"", r.""Id_Pregunta"", r.""Valor_Respuesta"" 
                               FROM ""Eventos_Respuestas"" r 
                               JOIN ""Eventos_Preguntas"" p ON r.""Id_Pregunta"" = p.""Id_Pregunta""
                               WHERE p.""Id_Evento"" = @id";
                    using (var cmdR = new NpgsqlCommand(sqlResp, conexion))
                    {
                        cmdR.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdR.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idAsis = (int)r["Id_Asistente"];
                                int idPreg = (int)r["Id_Pregunta"];
                                if (!diccionarioRespuestas.ContainsKey(idAsis)) diccionarioRespuestas[idAsis] = new Dictionary<int, string>();
                                diccionarioRespuestas[idAsis][idPreg] = r["Valor_Respuesta"].ToString();
                            }
                        }
                    }

                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        // =========================================================================
                        // HOJA 1: TOTALES (Resumen Ejecutivo)
                        // =========================================================================
                        var ws1 = wb.Worksheets.Add("Totales");
                        string sqlTotales = @"
                        SELECT 
                            (SELECT COUNT(*) FROM ""Eventos_B_Asistentes"" b JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" WHERE r.""Id_Evento"" = @id) as Inscritos,
                            (SELECT COALESCE(SUM(""Total_Neto""), 0) FROM ""Eventos_D_Transacciones"" WHERE ""Id_Evento"" = @id AND ""Completado"" = TRUE) as Recaudado,
                            (SELECT COALESCE(SUM(""Monto_Pagar""),0) FROM ""Eventos_C_Cuentas_Cobrar"" c JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente"" JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" WHERE r.""Id_Evento"" = @id AND c.""Pagado"" = FALSE) as Pendiente";

                        using (var cmd = new NpgsqlCommand(sqlTotales, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync()) if (await r.ReadAsync())
                            {
                                // 1. DATOS TÉCNICOS DEL EVENTO
                                ws1.Cell(1, 1).Value = "DATOS DEL EVENTO";
                                ws1.Range("A1:B1").Merge().Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);

                                ws1.Cell(2, 1).Value = "ID Evento:"; ws1.Cell(2, 2).Value = id;
                                ws1.Cell(3, 1).Value = "Nombre:"; ws1.Cell(3, 2).Value = tituloOriginal;
                                ws1.Cell(4, 1).Value = "Fecha de Inicio:"; ws1.Cell(4, 2).Value = fechaEvento.ToString("dd/MM/yyyy HH:mm");
                                ws1.Cell(5, 1).Value = "Estatus:";
                                ws1.Cell(5, 2).Value = eventoActivo ? "ACTIVO" : "CERRADO / FINALIZADO";
                                ws1.Cell(5, 2).Style.Font.SetFontColor(eventoActivo ? ClosedXML.Excel.XLColor.Green : ClosedXML.Excel.XLColor.Red).Font.SetBold();

                                // 2. RESUMEN FINANCIERO Y DE ASISTENCIA
                                ws1.Cell(8, 1).Value = "RESUMEN FINANCIERO Y ASISTENCIA";
                                ws1.Range("A8:B8").Merge().Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.AliceBlue);

                                ws1.Cell(9, 1).Value = "Personas Inscritas:"; ws1.Cell(9, 2).Value = (long)r["Inscritos"];
                                ws1.Cell(10, 1).Value = "Dinero Recaudado:"; ws1.Cell(10, 2).Value = (decimal)r["Recaudado"];
                                ws1.Cell(11, 1).Value = "Dinero por Cobrar:"; ws1.Cell(11, 2).Value = (decimal)r["Pendiente"];

                                ws1.Cell(10, 2).Style.NumberFormat.Format = "$ #,##0.00";
                                ws1.Cell(11, 2).Style.NumberFormat.Format = "$ #,##0.00";

                                ws1.Columns().AdjustToContents();
                            }
                        }

                        // =========================================================================
                        // HOJA 2: LISTA DE ASISTENTES (Logística y Entrada)
                        // =========================================================================
                        var ws2 = wb.Worksheets.Add("Lista de Asistentes");

                        // 1. AGREGAMOS LA NUEVA COLUMNA AL FINAL DEL ARREGLO
                        string[] h2 = { "Nombre Asistente", "Edad", "Género", "Modalidad", "Inscrito Por", "Correo de Contacto", "Teléfono", "Iglesia", "Municipio", "Localidad", "Ubicación (Maps)", "Costo Total", "Pagado", "Adeudo", "Estado Pago", "Pagó en Efectivo" };

                        for (int i = 0; i < h2.Length; i++) ws2.Cell(1, i + 1).Value = h2[i];
                        for (int i = 0; i < preguntasEvento.Count; i++) ws2.Cell(1, h2.Length + i + 1).Value = preguntasEvento[i].Texto;

                        // 2. ACTUALIZAMOS EL SQL CON EL "CASE WHEN EXISTS" Y LA CORRECCIÓN DE TOTALES
                        string sqlAsistentes = @"
                        SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Edad"", b.""Genero"", 
                               COALESCE(s.""Nombre"", 'Entrada General') as Modalidad,
                               u.""NombreCompleto"" as Inscriptor, u.""Email"", u.""Telefono"",
                               'N/A' as Iglesia, 'N/A' as MunicipioNombre, 'N/A' as localidad, CAST(NULL AS TEXT) as mapa_url,
                               b.""Es_Pagado"",
                               COALESCE(s.""Costo"", e.""Costo_Entrada"") as CostoOficial,
                               
                               (SELECT COALESCE(SUM(COALESCE(log.""Monto_Original"", c.""Monto_Pagar"")), 0) 
                                FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                LEFT JOIN ""Eventos_Ingresos_Efectivo_Log"" log ON c.""Id_Cuenta"" = log.""Id_Cuenta"" 
                                WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as TotalCostoAsignado,
                                
                               (SELECT COALESCE(SUM(COALESCE(log.""Monto_Original"", c.""Monto_Pagar"")), 0) 
                                FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                LEFT JOIN ""Eventos_Ingresos_Efectivo_Log"" log ON c.""Id_Cuenta"" = log.""Id_Cuenta"" 
                                WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as TotalPagado,
                                
                               (SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE) as DeudaReal,
                               
                               CASE WHEN EXISTS (
                                   SELECT 1 FROM ""Eventos_Ingresos_Efectivo_Log"" log 
                                   JOIN ""Eventos_C_Cuentas_Cobrar"" cx ON log.""Id_Cuenta"" = cx.""Id_Cuenta"" 
                                   WHERE cx.""Id_Asistente"" = b.""Id_Asistente""
                               ) THEN 'SÍ' ELSE 'NO' END as PagoEfectivo
                               
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                        JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""

                        WHERE r.""Id_Evento"" = @id ORDER BY b.""Nombre_Completo"" ASC";

                        int f2 = 2;
                        using (var cmd = new NpgsqlCommand(sqlAsistentes, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    int idAsis = (int)r["Id_Asistente"];
                                    decimal costoOficial = (decimal)r["CostoOficial"];
                                    decimal costoRealAsignado = (decimal)r["TotalCostoAsignado"];
                                    decimal pagado = (decimal)r["TotalPagado"];
                                    decimal deudaReal = (decimal)r["DeudaReal"];
                                    decimal costoFinalColumna = costoRealAsignado > 0 ? costoRealAsignado : costoOficial;

                                    ws2.Cell(f2, 1).Value = r["Nombre_Completo"].ToString();
                                    ws2.Cell(f2, 2).Value = (int)r["Edad"];
                                    ws2.Cell(f2, 3).Value = r["Genero"].ToString() == "H" ? "Hombre" : "Mujer";
                                    ws2.Cell(f2, 4).Value = r["Modalidad"].ToString();
                                    ws2.Cell(f2, 5).Value = r["Inscriptor"].ToString();
                                    ws2.Cell(f2, 6).Value = r["Email"]?.ToString();
                                    ws2.Cell(f2, 7).Value = r["Telefono"]?.ToString();
                                    ws2.Cell(f2, 8).Value = r["Iglesia"]?.ToString() ?? "N/A";
                                    ws2.Cell(f2, 9).Value = r["MunicipioNombre"]?.ToString() ?? "N/A";
                                    ws2.Cell(f2, 10).Value = r["localidad"]?.ToString() ?? "N/A";

                                    if (r["mapa_url"] != DBNull.Value)
                                    {
                                        ws2.Cell(f2, 11).Value = "Abrir Mapa";
                                        ws2.Cell(f2, 11).SetHyperlink(new ClosedXML.Excel.XLHyperlink(r["mapa_url"].ToString()));
                                    }
                                    else { ws2.Cell(f2, 11).Value = "N/A"; }

                                    ws2.Cell(f2, 12).Value = costoFinalColumna;
                                    ws2.Cell(f2, 13).Value = pagado;
                                    ws2.Cell(f2, 14).Value = deudaReal;
                                    ws2.Cell(f2, 15).Value = (bool)r["Es_Pagado"] ? "LIQUIDADO" : "DEUDA";

                                    // 3. IMPRIMIMOS LA NUEVA CELDA AL FINAL (Columna 16)
                                    ws2.Cell(f2, 16).Value = r["PagoEfectivo"].ToString();

                                    // Formato a Negritas y Color para que resalte visualmente si pagó en efectivo
                                    if (r["PagoEfectivo"].ToString() == "SÍ")
                                    {
                                        ws2.Cell(f2, 16).Style.Font.SetBold().Font.SetFontColor(ClosedXML.Excel.XLColor.DarkGreen);
                                    }

                                    ws2.Cell(f2, 12).Style.NumberFormat.Format = "$ #,##0.00";
                                    ws2.Cell(f2, 13).Style.NumberFormat.Format = "$ #,##0.00";
                                    ws2.Cell(f2, 14).Style.NumberFormat.Format = "$ #,##0.00";

                                    for (int i = 0; i < preguntasEvento.Count; i++)
                                    {
                                        int pId = preguntasEvento[i].Id;
                                        ws2.Cell(f2, h2.Length + i + 1).Value = diccionarioRespuestas.ContainsKey(idAsis) && diccionarioRespuestas[idAsis].ContainsKey(pId) ? diccionarioRespuestas[idAsis][pId] : "";
                                    }
                                    f2++;
                                }
                            }
                        }
                        ws2.Columns().AdjustToContents();

                        // =========================================================================
                        // HOJA 3: INGRESOS Y COMPROBANTES (Libro de Caja)
                        // =========================================================================
                        var ws3 = wb.Worksheets.Add("Ingresos y Comprobantes");
                        string[] h3 = { "ID Pago", "Fecha Intento/Pago", "Monto Cobrado", "Descuento", "Monto Neto (Real)", "Método", "Estatus", "Folio / Referencia", "Personas que cubre este pago", "Nombre del Archivo (ZIP)" };
                        for (int i = 0; i < h3.Length; i++) ws3.Cell(1, i + 1).Value = h3[i];

                        string sqlIngresos = @"
                    SELECT t.""Id_Transaccion"", t.""Fecha_Pago_Aceptado"", t.""Fecha_Intento"", t.""Monto_Total"", t.""Monto_Descuento"", t.""Total_Neto"", 
                           CASE WHEN t.""EsTransferencia"" THEN 'Transferencia' ELSE 'Tarjeta' END as Metodo,
                           CASE 
                               WHEN t.""Estatus_Pago"" = 'paid' THEN 'Aprobado'
                               WHEN t.""Estatus_Pago"" = 'review' THEN 'En Revisión'
                               ELSE 'Pendiente / Abandonado'
                           END as EstatusTraducido,
                           COALESCE(t.""Ref_Pasarela"", t.""External_Reference"") as Referencia,
                           t.""External_Reference"", t.""Id_Archivo_Comprobante"",
                           (SELECT u2.""NombreCompleto"" FROM ""Sist_Usuarios"" u2 WHERE u2.""Id_Usuario"" = t.""Id_Usuario"") as Solicitante,
                           (SELECT string_agg(DISTINCT b2.""Nombre_Completo"", ', ') 
                            FROM ""Eventos_E_Pagos_Aplicados"" pa2 
                            JOIN ""Eventos_C_Cuentas_Cobrar"" c2 ON pa2.""Id_Cuenta"" = c2.""Id_Cuenta""
                            JOIN ""Eventos_B_Asistentes"" b2 ON c2.""Id_Asistente"" = b2.""Id_Asistente""
                            WHERE pa2.""Id_Transaccion"" = t.""Id_Transaccion"") as Personas
                    FROM ""Eventos_D_Transacciones"" t 
                    WHERE t.""Id_Evento"" = @id AND t.""Estatus_Pago"" IN ('paid', 'review') 
                    ORDER BY t.""Fecha_Intento"" DESC";

                        int f3 = 2;
                        using (var cmd = new NpgsqlCommand(sqlIngresos, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    string refExterna = r["External_Reference"]?.ToString() ?? r["Id_Transaccion"].ToString();
                                    string nombreLimpio = System.Text.RegularExpressions.Regex.Replace(r["Solicitante"]?.ToString() ?? "Anonimo", "[^a-zA-Z0-9]", "");
                                    string nombreArchivo = r["Id_Archivo_Comprobante"] != DBNull.Value ? $"{refExterna}_{nombreLimpio}.jpg/pdf" : "Sin Archivo";
                                    string estatus = r["EstatusTraducido"].ToString();

                                    ws3.Cell(f3, 1).Value = (int)r["Id_Transaccion"];
                                    ws3.Cell(f3, 2).Value = r["Fecha_Pago_Aceptado"] != DBNull.Value ? (DateTime)r["Fecha_Pago_Aceptado"] : (DateTime)r["Fecha_Intento"];
                                    ws3.Cell(f3, 3).Value = (decimal)r["Monto_Total"];
                                    ws3.Cell(f3, 4).Value = r["Monto_Descuento"] != DBNull.Value ? (decimal)r["Monto_Descuento"] : 0m;
                                    ws3.Cell(f3, 5).Value = r["Total_Neto"] != DBNull.Value ? (decimal)r["Total_Neto"] : 0m;
                                    ws3.Cell(f3, 6).Value = r["Metodo"].ToString();
                                    ws3.Cell(f3, 7).Value = estatus;
                                    ws3.Cell(f3, 8).Value = r["Referencia"].ToString();
                                    ws3.Cell(f3, 9).Value = r["Personas"]?.ToString();
                                    ws3.Cell(f3, 10).Value = nombreArchivo;

                                    // Formato de moneda
                                    ws3.Cell(f3, 3).Style.NumberFormat.Format = "$ #,##0.00";
                                    ws3.Cell(f3, 4).Style.NumberFormat.Format = "$ #,##0.00";
                                    ws3.Cell(f3, 5).Style.NumberFormat.Format = "$ #,##0.00";

                                    // Color de estatus para que el admin lo detecte rápido
                                    if (estatus == "Aprobado")
                                    {
                                        ws3.Cell(f3, 7).Style.Font.SetFontColor(ClosedXML.Excel.XLColor.ForestGreen).Font.SetBold();
                                    }
                                    else if (estatus == "En Revisión" || estatus == "Esperando Comprobante")
                                    {
                                        ws3.Cell(f3, 7).Style.Font.SetFontColor(ClosedXML.Excel.XLColor.DarkOrange).Font.SetBold();
                                    }
                                    else if (estatus == "Rechazado")
                                    {
                                        ws3.Cell(f3, 7).Style.Font.SetFontColor(ClosedXML.Excel.XLColor.Crimson).Font.SetBold();
                                    }

                                    f3++;
                                }
                            }
                        }
                        ws3.Columns().AdjustToContents();

                        // =========================================================================
                        // HOJA 4: ABONOS Y DEUDAS (Seguimiento de Pagos Diferidos)
                        // =========================================================================
                        var ws5 = wb.Worksheets.Add("Abonos y Deudas");
                        string[] h5 = { "Nombre Asistente", "Modalidad", "Concepto (Plazo)", "Monto Plazo", "Fecha Límite", "Estatus Plazo", "Fecha Pagado" };
                        for (int i = 0; i < h5.Length; i++) ws5.Cell(1, i + 1).Value = h5[i];

                        string sqlAbonos = @"
                    SELECT b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as Modalidad,
                           cal.""Nombre_Concepto"", cal.""Fecha_Limite"", c.""Monto_Pagar"", c.""Pagado"", c.""Fecha_Pagado""
                    FROM ""Eventos_C_Cuentas_Cobrar"" c
                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                    JOIN ""Eventos_B_Asistentes"" b ON c.""Id_Asistente"" = b.""Id_Asistente""
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                    LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                    WHERE r.""Id_Evento"" = @id
                    ORDER BY b.""Nombre_Completo"", cal.""Numero_Pago"" ASC";

                        int f5 = 2;
                        using (var cmd = new NpgsqlCommand(sqlAbonos, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    ws5.Cell(f5, 1).Value = r["Nombre_Completo"].ToString();
                                    ws5.Cell(f5, 2).Value = r["Modalidad"].ToString();
                                    ws5.Cell(f5, 3).Value = r["Nombre_Concepto"]?.ToString() ?? "Abono";
                                    ws5.Cell(f5, 4).Value = (decimal)r["Monto_Pagar"];
                                    ws5.Cell(f5, 5).Value = (DateTime)r["Fecha_Limite"];
                                    ws5.Cell(f5, 6).Value = (bool)r["Pagado"] ? "PAGADO" : "PENDIENTE";
                                    if (r["Fecha_Pagado"] != DBNull.Value) ws5.Cell(f5, 7).Value = (DateTime)r["Fecha_Pagado"];

                                    ws5.Cell(f5, 4).Style.NumberFormat.Format = "$ #,##0.00";
                                    f5++;
                                }
                            }
                        }
                        ws5.Columns().AdjustToContents();

                        // =========================================================================
                        // HOJA 5: HABITACIONES (Si aplica)
                        // =========================================================================
                        var ws4 = wb.Worksheets.Add("Habitaciones");
                        string[] h4 = { "Zona", "Cuarto / Cabaña", "Género Asignado", "Ocupante", "Modalidad" };
                        for (int i = 0; i < h4.Length; i++) ws4.Cell(1, i + 1).Value = h4[i];

                        string sqlHab = @"
                    SELECT z.""Nombre"" as Zona, g.""Nombre"" as Cuarto, COALESCE(g.""Genero_Asignado"", 'Mixto') as Genero, 
                           b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'General') as Modalidad
                    FROM ""Eventos_I_Alojamiento_Asignaciones"" a
                    JOIN ""Eventos_G_Alojamientos"" g ON a.""Id_Alojamiento"" = g.""Id_Alojamiento""
                    JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                    JOIN ""Eventos_B_Asistentes"" b ON a.""Id_Asistente"" = b.""Id_Asistente""
                    LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                    WHERE z.""Id_Evento"" = @id ORDER BY z.""Nombre"", g.""Nombre"", b.""Nombre_Completo""";

                        int f4 = 2;
                        using (var cmd = new NpgsqlCommand(sqlHab, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    ws4.Cell(f4, 1).Value = r["Zona"].ToString();
                                    ws4.Cell(f4, 2).Value = r["Cuarto"].ToString();
                                    ws4.Cell(f4, 3).Value = r["Genero"].ToString() == "H" ? "Hombres" : (r["Genero"].ToString() == "M" ? "Mujeres" : "Mixto");
                                    ws4.Cell(f4, 4).Value = r["Nombre_Completo"].ToString();
                                    ws4.Cell(f4, 5).Value = r["Modalidad"].ToString();
                                    f4++;
                                }
                            }
                        }
                        ws4.Columns().AdjustToContents();

                        // =========================================================================
                        // HOJA 6: EXTRAS SOLICITADOS
                        // =========================================================================
                        var ws6 = wb.Worksheets.Add("Extras Solicitados");
                        string[] h6 = { "Nombre Asistente", "Modalidad", "Producto/Extra", "Cantidad", "Precio Unitario", "Total", "Estado Pago", "Inscriptor" };
                        for (int i = 0; i < h6.Length; i++) ws6.Cell(1, i + 1).Value = h6[i];

                        string sqlExtras = @"
                    SELECT b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as Modalidad,
                           pe_cat.""Nombre_Producto"" as Producto, pe.""Cantidad"", pe.""Precio_Unitario"", pe.""Total"",
                           (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as PagosHechos,
                           (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as TotalPagos,
                           u.""NombreCompleto"" as Inscriptor
                    FROM ""Eventos_Asistentes_ProductosExtra"" pe
                    JOIN ""Eventos_Catalogo_ProductosExtra"" pe_cat ON pe.""Id_ProductoExtra"" = pe_cat.""Id_ProductoExtra""
                    JOIN ""Eventos_B_Asistentes"" b ON pe.""Id_Asistente"" = b.""Id_Asistente""
                    JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                    JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                    LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                    WHERE r.""Id_Evento"" = @id
                    ORDER BY pe_cat.""Nombre_Producto"", b.""Nombre_Completo"" ASC";

                        int f6 = 2;
                        using (var cmd = new NpgsqlCommand(sqlExtras, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    int pagosHechos = Convert.ToInt32(r["PagosHechos"]);
                                    int totalPagos = Convert.ToInt32(r["TotalPagos"]);
                                    string estadoPago = pagosHechos == totalPagos && totalPagos > 0 ? "LIQUIDADO" : (pagosHechos > 0 ? "ABONADO" : "DEUDA");

                                    ws6.Cell(f6, 1).Value = r["Nombre_Completo"].ToString();
                                    ws6.Cell(f6, 2).Value = r["Modalidad"].ToString();
                                    ws6.Cell(f6, 3).Value = r["Producto"].ToString();
                                    ws6.Cell(f6, 4).Value = (int)r["Cantidad"];
                                    ws6.Cell(f6, 5).Value = (decimal)r["Precio_Unitario"];
                                    ws6.Cell(f6, 6).Value = (decimal)r["Total"];
                                    ws6.Cell(f6, 7).Value = estadoPago;
                                    ws6.Cell(f6, 8).Value = r["Inscriptor"].ToString();

                                    ws6.Cell(f6, 5).Style.NumberFormat.Format = "$ #,##0.00";
                                    ws6.Cell(f6, 6).Style.NumberFormat.Format = "$ #,##0.00";
                                    f6++;
                                }
                            }
                        }
                        ws6.Columns().AdjustToContents();

                        // Formato General para todas las hojas
                        foreach (var sheet in wb.Worksheets)
                        {
                            var range = sheet.Range(1, 1, 1, sheet.LastColumnUsed()?.ColumnNumber() ?? 5);
                            range.Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                            sheet.SheetView.FreezeRows(1);
                        }

                        // Guardar y retornar
                        using (var stream = new MemoryStream())
                        {
                            wb.SaveAs(stream);
                            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_{tituloEvento}.xlsx");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("PanelControl", new { sid = sid });
            }
        }
        private async Task<string> SubirImagenCloudinary(IFormFile foto, string folderPath)
        {
            using var memoryStream = new MemoryStream();
            using (var image = await SixLabors.ImageSharp.Image.LoadAsync(foto.OpenReadStream()))
            {
                const int MaxWidth = 1200;
                if (image.Width > MaxWidth)
                {
                    int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                    image.Mutate(x => x.Resize(MaxWidth, newHeight));
                }
                var encoder = new JpegEncoder { Quality = 75 };
                await image.SaveAsync(memoryStream, encoder);
            }

            memoryStream.Position = 0;
            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(foto.FileName, memoryStream),
                Folder = folderPath,
                Transformation = new Transformation().FetchFormat("auto"),

                // --- BLINDAJE CONTRA NOMBRES REPETIDOS ---
                UseFilename = false,     // Ignora el nombre original (ej. "foto1.jpg")
                UniqueFilename = true    // Forza a Cloudinary a generar un Hash único (ej. "8xnz912m...")
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }

        /// <summary>
        /// Muestra la pantalla para cargar el archivo Excel
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> CargaExterna(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var id = Funciones.DesencriptarId(sid);
            var modelo = new EventoItemViewModel();
            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();
                var cmd = new NpgsqlCommand(@"SELECT ""Titulo"", ""Id_Evento"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con);
                cmd.Parameters.AddWithValue("@id", id);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        modelo.Titulo = r["Titulo"].ToString();
                        modelo.Id_Evento = (int)r["Id_Evento"];
                        modelo.Id_Encriptado_Evento = sid;
                    }
                    else
                    {
                        MostrarMensaje("Error", "No se ha encontrado el evento", TipoMensaje.Error);
                        return RedirectToAction("Index"); 
                    }
                }
            }
            return View(modelo);
        }

        /// <summary>
        /// Genera la plantilla Excel para registros externos, incluye validación BUSCARV, logo proporcional y modalidades ordenadas.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DescargarPlantillaExterna(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idEvento = Funciones.DesencriptarId(sid);
            string tituloEvento = "";
            var modalidades = new List<dynamic>();
            var preguntas = new List<dynamic>();

            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();

                var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con);
                cmdEv.Parameters.AddWithValue("@id", idEvento);
                tituloEvento = (string)await cmdEv.ExecuteScalarAsync();

                // ORDENAMIENTO POR ID ASCENDENTE APLICADO AQUÍ
                var cmdSub = new NpgsqlCommand(@"SELECT ""Id_Subtipo"", ""Nombre"", ""Costo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento"" = @id AND ""Activo"" = TRUE ORDER BY ""Id_Subtipo"" ASC", con);
                cmdSub.Parameters.AddWithValue("@id", idEvento);
                using (var r = await cmdSub.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync()) modalidades.Add(new { Id = (int)r["Id_Subtipo"], Nombre = r["Nombre"].ToString(), Costo = (decimal)r["Costo"] });
                }

                var cmdPreg = new NpgsqlCommand(@"SELECT ""Id_Pregunta"", ""Texto_Pregunta"" FROM ""Eventos_Preguntas"" WHERE ""Id_Evento"" = @id ORDER BY ""Id_Pregunta"" ASC", con);
                cmdPreg.Parameters.AddWithValue("@id", idEvento);
                using (var r = await cmdPreg.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync()) preguntas.Add(new { Id = (int)r["Id_Pregunta"], Texto = r["Texto_Pregunta"].ToString() });
                }
            }

            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                // ==========================================
                // HOJA 1: CAPTURA DE REGISTROS
                // ==========================================
                var ws = wb.Worksheets.Add("Registros_Externos");

                // --- Alturas de fila más compactas ---
                ws.Row(1).Height = 60; // Fila principal ancha para el logo
                ws.Row(2).Height = 20;
                ws.Row(3).Height = 25;
                ws.Row(4).Height = 30; // Encabezados de Columnas

                // --- Fondo barra superior oscura (Se mezcla con el logo) ---
                var rngFila1 = ws.Range("A1:G1");
                rngFila1.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#16212e")); // Azul marino muy oscuro
                rngFila1.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.None;

                // --- Inserción del Logo Proporcional ---
                var env = HttpContext.RequestServices.GetService<IWebHostEnvironment>();
                if (env != null)
                {
                    string logoPath = Path.Combine(env.WebRootPath, "Images", "logo.png");
                    if (System.IO.File.Exists(logoPath))
                    {
                        var image = ws.AddPicture(logoPath).MoveTo(ws.Cell("A1"), 15, 10);

                        // Utilizamos Scale en lugar de Width/Height para que jamás se estire o distorsione
                        // 0.5 reduce la imagen al 50% de su tamaño real manteniendo la proporción exacta.
                        image.Scale(0.5);
                        image.Height = 60;
                        image.Width = 60;
                    }
                }

                // --- Títulos e Instrucciones ---
                ws.Cell("B1").Value = $"PLANTILLA DE REGISTRO EXTERNO: {tituloEvento.ToUpper()}";
                var rngTitle = ws.Range("B1:F1");
                rngTitle.Merge();
                rngTitle.Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                rngTitle.Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Left);
                rngTitle.Style.Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);

                ws.Cell("A2").Value = "INSTRUCCIONES: Llena los datos obligatorios marcados con (*). Para el Género, utiliza 'H' (Hombre) o 'M' (Mujer).";
                ws.Cell("A3").Value = "Ingresa el 'ID Modalidad' y la columna de 'Nombre Validado' se llenará automáticamente. Si muestra ❌, el ID es incorrecto.";
                ws.Range("A2:I2").Merge().Style.Font.SetItalic().Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#34495e")).Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);
                ws.Range("A3:I3").Merge().Style.Font.SetItalic().Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#c0392b")).Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);

                // --- Encabezados de Columnas (Ahora en Fila 4) ---
                int filaHead = 4;
                ws.Cell(filaHead, 1).Value = "Nombre Completo *";
                ws.Cell(filaHead, 2).Value = "Correo Electrónico Para Enviar Enlace de Pago";
                ws.Cell(filaHead, 3).Value = "Teléfono de Contacto";
                ws.Cell(filaHead, 4).Value = "Clave Modalidad";
                ws.Cell(filaHead, 5).Value = "Nombre Validado (Autocompletado)";
                ws.Cell(filaHead, 6).Value = "Edad";
                ws.Cell(filaHead, 7).Value = "Género (H/M)";

                int colActual = 8;
                foreach (var p in preguntas)
                {
                    // Se concatena el ID al texto de la pregunta para la sincronización
                    ws.Cell(filaHead, colActual).Value = $"{p.Id}. {p.Texto}";
                    colActual++;
                }

                // Estilos del encabezado
                var rngHeaders = ws.Range(filaHead, 1, filaHead, colActual - 1);
                rngHeaders.Style.Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#27ae60"));
                rngHeaders.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#EBF1DE"));
                rngHeaders.Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Center);
                rngHeaders.Style.Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);
                rngHeaders.Style.Alignment.SetWrapText(true);

                ws.Cell(filaHead, 5).Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#FABF8F"));
                ws.Cell(filaHead, 5).Style.Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#FFFFFF"));

                // --- Anchos ---
                ws.Column(1).Width = 35;
                ws.Column(2).Width = 35;
                ws.Column(3).Width = 20;
                ws.Column(4).Width = 15;
                ws.Column(5).Width = 35;
                ws.Column(6).Width = 12;
                ws.Column(7).Width = 15;
                for (int c = 8; c < colActual; c++) ws.Column(c).Width = 30;

                // ==========================================
                // INYECCIÓN DE FÓRMULA BUSCARV (VLOOKUP)
                // ==========================================
                // Los datos empiezan en la fila 5
                for (int i = 5; i <= 1000; i++)
                {
                    var celdaFormula = ws.Cell(i, 5);

                    // 1. Aplicamos la fórmula BUSCARV (VLOOKUP)
                    celdaFormula.FormulaA1 = $"IF(ISBLANK(D{i}), \"\", IFERROR(VLOOKUP(D{i}, 'Catálogo Modalidades'!A:B, 2, FALSE), \"❌ ID no existe\"))";

                    // 2. Fondo Gris Perla: Elegante, limpio y denota "campo automático"
                    celdaFormula.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#F2F4F4"));

                    // 3. Texto Azul Corporativo e Itálica: Para diferenciar visualmente el resultado del sistema
                    celdaFormula.Style.Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#2E86C1"));
                    celdaFormula.Style.Font.SetItalic();

                    // 4. Bordes Plata: Sutiles pero definidos para dar profundidad y evitar el efecto plano
                    celdaFormula.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                    celdaFormula.Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("#BDC3C7");

                    // 5. Alineación vertical
                    celdaFormula.Style.Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);
                }

                // ==========================================
                // HOJA 2: CATÁLOGO DE MODALIDADES
                // ==========================================
                var wsCat = wb.Worksheets.Add("Catálogo Modalidades");
                wsCat.Cell(1, 1).Value = "ID Modalidad";
                wsCat.Cell(1, 2).Value = "Nombre de la Modalidad";
                wsCat.Cell(1, 3).Value = "Costo Asignado";

                var rngCat = wsCat.Range("A1:C1");
                rngCat.Style.Font.SetBold().Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                rngCat.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2980b9"));
                wsCat.Row(1).Height = 25;

                int row = 2;
                foreach (var m in modalidades)
                {
                    wsCat.Cell(row, 1).Value = m.Id;
                    wsCat.Cell(row, 2).Value = m.Nombre;
                    wsCat.Cell(row, 3).Value = m.Costo;
                    wsCat.Cell(row, 3).Style.NumberFormat.Format = "$ #,##0.00";
                    wsCat.Cell(row, 1).Style.Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Center);
                    row++;
                }

                wsCat.Column(1).Width = 15;
                wsCat.Column(2).Width = 40;
                wsCat.Column(3).Width = 20;

                using (var stream = new MemoryStream())
                {
                    wb.SaveAs(stream);
                    return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Plantilla_Externa_{tituloEvento.Replace(" ", "_")}.xlsx");
                }
            }
        }

        /// <summary>
        /// Procesa el archivo Excel de registros externos adaptado a la fila 6 de inicio por el nuevo Layout con Logo.
        /// </summary>
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarCargaExterna(string sid, IFormFile archivoExcel)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idEvento = Funciones.DesencriptarId(sid);
            int idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            int registradosExitosos = 0;
            if (archivoExcel == null || archivoExcel.Length == 0)
            {
                MostrarMensaje("Error", "El archivo está vacío o no es válido.", TipoMensaje.Error);
                return RedirectToAction("CargaExterna", new { sid = sid });
            }

            // Solo permitir Excel para evitar que ClosedXML falle
            var extension = Path.GetExtension(archivoExcel.FileName).ToLower();
            if (extension != ".xlsx")
            {
                MostrarMensaje("Error", "Formato inválido. Sube un archivo Excel (.xlsx).", TipoMensaje.Error);
                return RedirectToAction("CargaExterna", new { sid = sid });
            }

            var correosAEnviar = new List<(string Correo, string Nombre, string Token, string TituloEvento)>();

            try
            {
                using (var stream = new MemoryStream())
                {
                    await archivoExcel.CopyToAsync(stream);
                    using (var wb = new ClosedXML.Excel.XLWorkbook(stream))
                    {
                        var ws = wb.Worksheet("Registros_Externos");

                        // filtramos RowNumber > 4 porque los datos empiezan en la fila 5
                        var rows = ws.RowsUsed().Where(r => r.RowNumber() > 4).ToList();
                        int cantidadNuevos = rows.Count(r => !string.IsNullOrWhiteSpace(r.Cell(1).GetString()));

                        if (cantidadNuevos == 0)
                        {
                            MostrarMensaje("Archivo Vacío", "No se encontraron registros para procesar debajo de los encabezados.", TipoMensaje.Alerta);
                            return RedirectToAction("CargaExterna", new { sid = sid });
                        }

                        // Mapeo de columnas con el ID de la pregunta desde los encabezados (Fila 4)
                        var lastCol = ws.LastColumnUsed().ColumnNumber();
                        var mapeoPreguntas = new Dictionary<int, int>();

                        for (int c = 8; c <= lastCol; c++)
                        {
                            string headerText = ws.Cell(4, c).GetString();
                            if (!string.IsNullOrWhiteSpace(headerText) && headerText.Contains("."))
                            {
                                string idString = headerText.Split('.')[0].Trim();
                                if (int.TryParse(idString, out int idPreguntaMapeada))
                                {
                                    mapeoPreguntas.Add(c, idPreguntaMapeada);
                                }
                            }
                        }

                        using (var conexion = new NpgsqlConnection(_cadenaConexion))
                        {
                            await conexion.OpenAsync();

                            if (await ValidarEventoBloqueado(idEvento, conexion))
                            {
                                MostrarMensaje("Evento Bloqueado", "No se pueden realizar cargas masivas de Excel porque el evento está bloqueado.", TipoMensaje.Error);
                                return RedirectToAction("CargaExterna", new { sid = sid });
                            }
                            var preguntasIds = new List<int>();
                            using (var cmdP = new NpgsqlCommand(@"SELECT ""Id_Pregunta"" FROM ""Eventos_Preguntas"" WHERE ""Id_Evento"" = @id ORDER BY ""Id_Pregunta"" ASC", conexion))
                            {
                                cmdP.Parameters.AddWithValue("@id", idEvento);
                                using (var r = await cmdP.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync()) preguntasIds.Add((int)r["Id_Pregunta"]);
                                }
                            }

                            using (var trans = await conexion.BeginTransactionAsync())
                            {
                                try
                                {
                                    // Valida disponibilidad inyectando el bloqueo de seguridad transaccional
                                    var disp = await ConsultarDisponibilidadEvento(idEvento, conexion, trans, forUpdate: true);

                                    if (disp == null) throw new Exception("El evento no existe.");
                                    if (!disp.EventoActivo) throw new Exception("El evento ya no está activo o ha finalizado.");
                                    if (!disp.PermitirPago) throw new Exception("El evento no permite pagos. Habilítalos primero.");

                                    if ((disp.OcupadosGlobal + cantidadNuevos) > disp.CupoMaximoEvento)
                                        throw new Exception($"Cupo Excedido. Quedan {disp.DisponiblesGlobal} lugares y el archivo trae {cantidadNuevos}.");

                                    string tituloEvento = disp.Titulo;
                                    decimal costoGeneralEvento = disp.CostoEntrada;

                                    var dictSubtipos = new Dictionary<int, (decimal Costo, int Cupo, int Ocupados, string Nombre)>();
                                    foreach (var m in disp.Modalidades)
                                    {
                                        dictSubtipos.Add(m.IdSubtipo, (m.Costo, m.CupoTotal, m.LugaresOcupados, m.NombreModalidad));
                                    }
                                    bool eventoRequiereSubtipo = dictSubtipos.Any();

                                    // Control de NOMBRES duplicados en el mismo archivo Excel
                                    var nombresEnMemoria = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                    foreach (var fila in rows)
                                    {
                                        string nombre = fila.Cell(1).GetString()?.Trim();
                                        string correo = fila.Cell(2).GetString()?.Trim();
                                        string telefono = fila.Cell(3).GetString()?.Trim();

                                        if (string.IsNullOrEmpty(nombre)) continue;

                                        // Prevención de duplicados por NOMBRE
                                        if (!nombresEnMemoria.Add(nombre))
                                            throw new Exception($"Fila {fila.RowNumber()}: El asistente '{nombre}' viene repetido varias veces en este Excel.");

                                        // Prevenir duplicados contra la base de datos (por Nombre_Completo)
                                        using (var cmdCheckDb = new NpgsqlCommand(@"SELECT 1 FROM ""Eventos_B_Asistentes"" b JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" WHERE r.""Id_Evento"" = @idEv AND LOWER(b.""Nombre_Completo"") = @nom LIMIT 1", conexion, trans))
                                        {
                                            cmdCheckDb.Parameters.AddWithValue("@idEv", idEvento);
                                            cmdCheckDb.Parameters.AddWithValue("@nom", nombre.ToLower());
                                            if (await cmdCheckDb.ExecuteScalarAsync() != null)
                                                throw new Exception($"Fila {fila.RowNumber()}: El asistente '{nombre}' ya está registrado en este evento.");
                                        }

                                        int? idSubtipo = null;
                                        decimal costoFinalAplicar = costoGeneralEvento;

                                        if (fila.Cell(4).TryGetValue(out int subId) && subId > 0) idSubtipo = subId;

                                        if (eventoRequiereSubtipo && !idSubtipo.HasValue)
                                            throw new Exception($"Fila {fila.RowNumber()}: El asistente '{nombre}' no tiene una Modalidad asignada.");

                                        if (idSubtipo.HasValue)
                                        {
                                            if (!dictSubtipos.ContainsKey(idSubtipo.Value)) throw new Exception($"Fila {fila.RowNumber()}: ID de modalidad '{idSubtipo.Value}' no existe.");
                                            var subData = dictSubtipos[idSubtipo.Value];
                                            if ((subData.Ocupados + 1) > subData.Cupo) throw new Exception($"Fila {fila.RowNumber()}: Modalidad '{subData.Nombre}' llena.");
                                            dictSubtipos[idSubtipo.Value] = (subData.Costo, subData.Cupo, subData.Ocupados + 1, subData.Nombre);
                                            costoFinalAplicar = subData.Costo;
                                        }

                                        // El asistente debe responder TODAS las preguntas del evento
                                        foreach (int idPregunta in preguntasIds)
                                        {
                                            // Buscamos en qué columna del Excel quedó mapeada esta pregunta
                                            int columnaPregunta = mapeoPreguntas.FirstOrDefault(m => m.Value == idPregunta).Key;

                                            // Si la columna ni siquiera viene en el Excel (usaron una plantilla vieja)
                                            if (columnaPregunta == 0)
                                                throw new Exception($"Fila {fila.RowNumber()}: El archivo Excel no incluye la columna para la pregunta ID {idPregunta}. Por favor, descarga la plantilla actualizada.");

                                            // Extraemos el valor de la celda
                                            string respuestaTexto = fila.Cell(columnaPregunta).GetString()?.Trim();

                                            // Si la celda está vacía, abortamos
                                            if (string.IsNullOrEmpty(respuestaTexto))
                                                throw new Exception($"Fila {fila.RowNumber()}: El asistente '{nombre}' dejó en blanco una de las preguntas obligatorias del evento.");
                                        }

                                        int edadAsistente = 18;
                                        if (int.TryParse(fila.Cell(6).GetString()?.Trim(), out int parseEdad) && parseEdad > 0)
                                            edadAsistente = parseEdad;

                                        string generoOriginal = fila.Cell(7).GetString()?.Trim().ToUpper();
                                        string generoAsistente = (generoOriginal == "M" || generoOriginal == "MUJER" || generoOriginal == "FEMENINO") ? "M" : "H";

                                        int idRegistro = 0;
                                        using (var cmdR = new NpgsqlCommand(@"INSERT INTO ""Eventos_A_Registros"" (""Id_Evento"", ""Id_Usuario"", ""Fecha_Registro"") VALUES (@ev, @usr, NOW()) RETURNING ""Id_Registro""", conexion, trans))
                                        {
                                            cmdR.Parameters.AddWithValue("@ev", idEvento);
                                            cmdR.Parameters.AddWithValue("@usr", idAdmin);
                                            idRegistro = (int)await cmdR.ExecuteScalarAsync();
                                        }

                                        string token = GenerarTokenAmigable(nombre);
                                        string sqlAsis = @"INSERT INTO ""Eventos_B_Asistentes"" 
                                          (""Id_Registro"", ""Nombre_Completo"", ""Etiqueta_Grupo"", ""Token_Pago_Externo"", ""Edad"", ""Genero"", ""Id_Subtipo"", ""Es_Pagado"", ""Correo_Externo"", ""Telefono_Externo"", ""Es_Carga_Masiva"") 
                                          VALUES (@reg, @nom, @grupo, @tok, @edad, @gen, @sub, FALSE, @email, @tel, TRUE) 
                                          RETURNING ""Id_Asistente""";
                                        int idAsistente = 0;
                                        using (var cmdA = new NpgsqlCommand(sqlAsis, conexion, trans))
                                        {
                                            cmdA.Parameters.AddWithValue("@reg", idRegistro);
                                            cmdA.Parameters.AddWithValue("@nom", nombre);
                                            cmdA.Parameters.AddWithValue("@grupo", "Externo");
                                            cmdA.Parameters.AddWithValue("@tok", token);
                                            cmdA.Parameters.AddWithValue("@edad", edadAsistente);
                                            cmdA.Parameters.AddWithValue("@gen", generoAsistente);
                                            cmdA.Parameters.AddWithValue("@sub", (object)idSubtipo ?? DBNull.Value);
                                            cmdA.Parameters.AddWithValue("@email", (object)correo ?? DBNull.Value);
                                            cmdA.Parameters.AddWithValue("@tel", (object)telefono ?? DBNull.Value);
                                            idAsistente = (int)await cmdA.ExecuteScalarAsync();
                                        }

                                        // Inserción dinámica sincronizada usando mapeoPreguntas
                                        foreach (var mapeo in mapeoPreguntas)
                                        {
                                            int columnaPregunta = mapeo.Key;
                                            int idPregunta = mapeo.Value;

                                            // Validación adicional de seguridad para que el ID exista en las preguntas del evento
                                            if (!preguntasIds.Contains(idPregunta)) continue;

                                            string respuestaTexto = fila.Cell(columnaPregunta).GetString()?.Trim();

                                            if (!string.IsNullOrEmpty(respuestaTexto))
                                            {
                                                using (var cmdResp = new NpgsqlCommand(@"INSERT INTO ""Eventos_Respuestas"" (""Id_Asistente"", ""Id_Pregunta"", ""Valor_Respuesta"") VALUES (@idA, @idP, @val)", conexion, trans))
                                                {
                                                    cmdResp.Parameters.AddWithValue("@idA", idAsistente);
                                                    cmdResp.Parameters.AddWithValue("@idP", idPregunta);
                                                    cmdResp.Parameters.AddWithValue("@val", respuestaTexto);
                                                    await cmdResp.ExecuteNonQueryAsync();
                                                }
                                            }
                                        }

                                        string sqlDeuda = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"")
                                                            SELECT @idA, ""Id_Calendario"", 
                                                            CASE WHEN (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) > 0 THEN
                                                                ROUND(@costo * ( CAST(""Monto_Base"" AS DECIMAL) / (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) ), 2)
                                                            ELSE @costo END, FALSE
                                                            FROM ""Eventos_Calendario"" 
                                                            WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))
                                                            ORDER BY ""Numero_Pago"" ASC";

                                        using (var cmdD = new NpgsqlCommand(sqlDeuda, conexion, trans))
                                        {
                                            cmdD.Parameters.AddWithValue("@idA", idAsistente);
                                            cmdD.Parameters.AddWithValue("@ev", idEvento);
                                            cmdD.Parameters.AddWithValue("@costo", costoFinalAplicar);
                                            cmdD.Parameters.Add(new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)idSubtipo ?? DBNull.Value });
                                            if (await cmdD.ExecuteNonQueryAsync() == 0)
                                                await new NpgsqlCommand($"INSERT INTO \"Eventos_C_Cuentas_Cobrar\" (\"Id_Asistente\", \"Monto_Pagar\",\"Id_Calendario\", \"Pagado\") VALUES ({idAsistente}, {costoFinalAplicar}, NULL, FALSE)", conexion, trans).ExecuteNonQueryAsync();
                                        }

                                        if (!string.IsNullOrEmpty(correo) && correo.Contains("@"))
                                            correosAEnviar.Add((correo, nombre, token, tituloEvento));

                                        registradosExitosos++;
                                    }
                                    await trans.CommitAsync();
                                }
                                catch (Exception) { await trans.RollbackAsync(); throw; }
                            }
                        }
                    }

                    foreach (var mData in correosAEnviar)
                    {
                        string linkPago = Url.Action("Pagar", "Eventos", new { token = mData.Token }, Request.Scheme);
                        string htmlSoporte = $@"
        <div style='font-family: sans-serif; border: 1px solid #eee; padding: 20px; border-radius: 10px; max-width: 600px; margin: 0 auto;'>
            <h2 style='color: #0d6efd;'>¡Hola {mData.Nombre}! Tienes un registro pendiente</h2>
            <p>Has sido registrado en la plataforma para asistir al evento: <strong>{mData.TituloEvento}</strong>.</p>
            <p>Para confirmar tu lugar y liquidar tu inscripción, por favor ingresa al siguiente enlace seguro de pago.</p>
            <p align='center' style='margin-top: 25px;'>
                <a href='{linkPago}' style='background: #198754; color: #fff; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; display: inline-block;'>
                    💳 Ir al Portal de Pago
                </a>
            </p>
            <hr style='border: 0; border-top: 1px solid #eee; margin: 20px 0;' />
            <small style='color: #777;'>Si tienes dudas, por favor contacta al administrador del evento. Tu código de identificación es: {mData.Token}</small>
        </div>";
                        try { await Funciones.EnviarCorreo(_configuration, mData.Correo, $"Registro a {mData.TituloEvento} - Pago Pendiente", htmlSoporte); } catch { }
                    }

                    MostrarMensaje("Carga Exitosa", $"Se importaron {registradosExitosos} registros externos correctamente.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Carga Abortada", "Se detuvo el proceso y NO se guardaron los datos. Motivo: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("RegistrosExternos", new { sid = sid });
        }

        /// <summary>
        /// Muestra exclusivamente los registros que entraron por Excel
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> RegistrosExternos(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);
            ViewBag.IdEventoEncriptado = sid;
            var lista = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = "SELECT b.\"Id_Asistente\", b.\"Nombre_Completo\", b.\"Correo_Externo\", b.\"Telefono_Externo\", b.\"Token_Pago_Externo\", " +
                                 "b.\"Es_Pagado\", r.\"Fecha_Registro\", u.\"NombreCompleto\" as \"AdminCarga\", " +
                                 "COALESCE(s.\"Nombre\", 'Entrada General') as \"Modalidad\", " +
                                 "(SELECT SUM(c.\"Monto_Pagar\") FROM \"Eventos_C_Cuentas_Cobrar\" c WHERE c.\"Id_Asistente\" = b.\"Id_Asistente\") as \"CostoTotal\" " +
                                 "FROM \"Eventos_B_Asistentes\" b " +
                                 "JOIN \"Eventos_A_Registros\" r ON b.\"Id_Registro\" = r.\"Id_Registro\" " +
                                 "JOIN \"Sist_Usuarios\" u ON r.\"Id_Usuario\" = u.\"Id_Usuario\" " +
                                 "LEFT JOIN \"Eventos_Subtipos\" s ON b.\"Id_Subtipo\" = s.\"Id_Subtipo\" " +
                                 "WHERE r.\"Id_Evento\" = @id AND b.\"Es_Carga_Masiva\" = TRUE " +
                                 "ORDER BY r.\"Fecha_Registro\" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                lista.Add(new
                                {
                                    IdAsistente = (int)r["Id_Asistente"], // <--- AGREGADO PARA EL BORRADO
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Correo = r["Correo_Externo"]?.ToString() ?? "N/D",
                                    Telefono = r["Telefono_Externo"]?.ToString() ?? "N/D",
                                    Token = r["Token_Pago_Externo"].ToString(),
                                    EsPagado = (bool)r["Es_Pagado"],
                                    Fecha = (DateTime)r["Fecha_Registro"],
                                    AdminCarga = r["AdminCarga"].ToString(),
                                    Modalidad = r["Modalidad"].ToString(),
                                    Costo = r["CostoTotal"] != DBNull.Value ? (decimal)r["CostoTotal"] : 0m
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(lista);
        }

        /// <summary>
        /// Elimina un asistente creado por carga masiva.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarRegistroExterno(int idAsistente, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            int idEvento = Funciones.DesencriptarId(sid);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "El evento está bloqueado. No se pueden eliminar registros externos.", TipoMensaje.Error);
                        return RedirectToAction("RegistrosExternos", new { sid = sid });
                    }

                    // 1. Verificar existencia y permisos (que el evento exista y este asistente le pertenezca al evento)
                    string sqlCheck = @"SELECT r.""Id_Registro""
                                        FROM ""Eventos_B_Asistentes"" b
                                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                                        WHERE b.""Id_Asistente"" = @idA AND r.""Id_Evento"" = @idEv AND b.""Es_Carga_Masiva"" = TRUE";

                    int idRegistro = 0;
                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idA", idAsistente);
                        cmd.Parameters.AddWithValue("@idEv", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync()) { idRegistro = (int)r[0]; }
                            else
                            {
                                MostrarMensaje("Error", "No tienes permiso o el registro no es válido.", TipoMensaje.Error);
                                return RedirectToAction("RegistrosExternos", new { sid = sid });
                            }
                        }
                    }

                    // 2. Validar que no tenga pagos completados (Tabla C)
                    long pagados = (long)await new NpgsqlCommand(
                        $"SELECT COUNT(*) FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\"={idAsistente} AND \"Pagado\"=TRUE",
                        conexion).ExecuteScalarAsync();

                    if (pagados > 0)
                    {
                        MostrarMensaje("Bloqueado", "No puedes eliminar a alguien que ya tiene pagos completados.", TipoMensaje.Error);
                        return RedirectToAction("RegistrosExternos", new { sid = sid });
                    }

                    // 3. Borrado Transaccional
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // Borrar posibles transacciones en proceso asociadas (Tabla E y D)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_E_Pagos_Aplicados\" WHERE \"Id_Cuenta\" IN (SELECT \"Id_Cuenta\" FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\"={idAsistente})", conexion, trans).ExecuteNonQueryAsync();

                            // Borrar Deuda (Tabla C)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_C_Cuentas_Cobrar\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // Borrar Respuestas dinámicas
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Respuestas\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            string sqlDelEncExterno = @"
                                DELETE FROM ""Encuestas_Respuestas_Detalle"" 
                                WHERE ""Id_Respuesta"" IN (SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" WHERE ""Id_Asistente_Evento"" = @idA);
                                DELETE FROM ""Encuestas_Respuestas_Header"" 
                                WHERE ""Id_Asistente_Evento"" = @idA;";
                            using (var cmdDelEnc = new NpgsqlCommand(sqlDelEncExterno, conexion, trans))
                            {
                                cmdDelEnc.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelEnc.ExecuteNonQueryAsync();
                            }

                            // Borrar Asistente (Tabla B)
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_Asistentes_ProductosExtra\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_B_Asistentes\" WHERE \"Id_Asistente\"={idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // Limpiar Registro Padre (Tabla A) si quedó vacío (Para no dejar huérfanos)
                            long quedan = (long)await new NpgsqlCommand($"SELECT COUNT(*) FROM \"Eventos_B_Asistentes\" WHERE \"Id_Registro\"={idRegistro}", conexion, trans).ExecuteScalarAsync();
                            if (quedan == 0)
                            {
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_A_Registros\" WHERE \"Id_Registro\"={idRegistro}", conexion, trans).ExecuteNonQueryAsync();
                            }

                            // Bitácora
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo,
                                Parametros.AccionesBitacora.Borrar,
                                $"Eliminó asistente externo ID {idAsistente} del Evento #{idEvento}",
                                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Eliminado", "Registro borrado correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            // Regresamos exactamente a la misma vista de registros Externos
            return RedirectToAction("RegistrosExternos", new { sid = sid });
        }

        /// <summary>
        /// Reenvía el correo con el enlace de pago a un registro Externo específico.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarCorreoExterno(int idAsistente, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Consultamos los datos de contacto del asistente
                    string sql = @"
                        SELECT b.""Nombre_Completo"", b.""Correo_Externo"", b.""Token_Pago_Externo"", e.""Titulo""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                        WHERE b.""Id_Asistente"" = @idA AND r.""Id_Evento"" = @idEv AND b.""Es_Carga_Masiva"" = TRUE";

                    string nombre = "", correo = "", token = "", tituloEvento = "";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idA", idAsistente);
                        cmd.Parameters.AddWithValue("@idEv", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                nombre = r["Nombre_Completo"].ToString();
                                correo = r["Correo_Externo"]?.ToString();
                                token = r["Token_Pago_Externo"].ToString();
                                tituloEvento = r["Titulo"].ToString();
                            }
                            else
                            {
                                MostrarMensaje("Error", "Registro no encontrado o no válido.", TipoMensaje.Error);
                                return RedirectToAction("RegistrosExternos", new { sid = sid });
                            }
                        }
                    }

                    // Validación del correo
                    if (string.IsNullOrWhiteSpace(correo) || !correo.Contains("@"))
                    {
                        MostrarMensaje("Atención", $"El asistente '{nombre}' no tiene un correo válido registrado ({correo}).", TipoMensaje.Alerta);
                        return RedirectToAction("RegistrosExternos", new { sid = sid });
                    }

                    // Generar la liga absoluta y enviar el correo
                    string linkPago = Url.Action("Pagar", "Eventos", new { token = token }, Request.Scheme);
                    string htmlSoporte = $@"
                        <div style='font-family: sans-serif; border: 1px solid #eee; padding: 20px; border-radius: 10px; max-width: 600px; margin: 0 auto;'>
                            <h2 style='color: #0d6efd;'>¡Hola {nombre}! Tienes un registro pendiente</h2>
                            <p>Has sido registrado en la plataforma para asistir al evento: <strong>{tituloEvento}</strong>.</p>
                            <p>Para confirmar tu lugar y liquidar tu inscripción, por favor ingresa al siguiente enlace seguro de pago.</p>
                            <p align='center' style='margin-top: 25px;'>
                                <a href='{linkPago}' style='background: #198754; color: #fff; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; display: inline-block;'>
                                    💳 Ir al Portal de Pago
                                </a>
                            </p>
                            <hr style='border: 0; border-top: 1px solid #eee; margin: 20px 0;' />
                            <small style='color: #777;'>Si tienes dudas, por favor contacta al administrador del evento. Tu código de identificación es: {token}</small>
                        </div>";

                    await Funciones.EnviarCorreo(_configuration, correo, $"Recordatorio de Pago - {tituloEvento}", htmlSoporte);

                    MostrarMensaje("Correo Enviado", $"Se reenvió el enlace de cobro exitosamente a {correo}.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo reenviar el correo: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("RegistrosExternos", new { sid = sid });
        }

        /// <summary>
        /// Muestra la interfaz de arrastrar y soltar para distribuir alojamientos.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DistribucionAlojamientos(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de administración de alojamientos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new DistribucionAlojamientosViewModel();
            modelo.IdEvento = Funciones.DesencriptarId(sid);
            modelo.IdEventoEncriptado = sid;
            modelo.LugaresCreadosPorSubtipo = new Dictionary<int, int>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // ====================================================================
                    // 1. CARGAR CONFIGURACIÓN Y AGRUPACIONES (NUEVO)
                    // ====================================================================

                    // Configuración del Portal
                    modelo.ConfigPortal = new ConfigPortalItem();
                    using (var cmdConf = new NpgsqlCommand(@"SELECT ""Portal_Activo"", ""Solo_Pagados"", ""Mostrar_Todas_Habitaciones"" FROM ""Eventos_ConfigAlojamiento"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdConf.Parameters.AddWithValue("@id", modelo.IdEvento);
                        using (var r = await cmdConf.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.ConfigPortal.Portal_Activo = (bool)r["Portal_Activo"];
                                modelo.ConfigPortal.Solo_Pagados = (bool)r["Solo_Pagados"];
                                modelo.ConfigPortal.MostrarTodasLasHabitaciones = r["Mostrar_Todas_Habitaciones"] != DBNull.Value && (bool)r["Mostrar_Todas_Habitaciones"];
                            }
                        }
                    }

                    // Clústeres (Agrupaciones)
                    string sqlAgrupaciones = @"
                SELECT a.""Id_Agrupacion"", a.""Clave_Tecnica"", a.""Nombre_Descriptivo"", s.""Nombre""
                FROM ""Eventos_K_AgrupaModalidades"" a
                LEFT JOIN ""Eventos_K_AgrupaModalidadesDetalle"" d ON a.""Id_Agrupacion"" = d.""Id_Agrupacion""
                LEFT JOIN ""Eventos_Subtipos"" s ON d.""Id_Subtipo"" = s.""Id_Subtipo""
                WHERE a.""Id_Evento"" = @id
                ORDER BY a.""Id_Agrupacion""";

                    using (var cmdAgr = new NpgsqlCommand(sqlAgrupaciones, conexion))
                    {
                        cmdAgr.Parameters.AddWithValue("@id", modelo.IdEvento);
                        using (var r = await cmdAgr.ExecuteReaderAsync())
                        {
                            int lastId = 0;
                            AgrupacionModalidadItem agAct = null;
                            while (await r.ReadAsync())
                            {
                                int idAgr = (int)r["Id_Agrupacion"];
                                if (idAgr != lastId)
                                {
                                    agAct = new AgrupacionModalidadItem
                                    {
                                        Id_Agrupacion = idAgr,
                                        Clave_Tecnica = r["Clave_Tecnica"].ToString(),
                                        Nombre_Descriptivo = r["Nombre_Descriptivo"].ToString()
                                    };
                                    modelo.Agrupaciones.Add(agAct);
                                    lastId = idAgr;
                                }
                                if (r["Nombre"] != DBNull.Value) agAct.NombresSubtipos.Add(r["Nombre"].ToString());
                            }
                        }
                    }

                    // ====================================================================
                    // 2. CARGAR VISTA (Lectura estándar)
                    // ====================================================================
                    var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion);
                    cmdEv.Parameters.AddWithValue("@id", modelo.IdEvento);
                    modelo.TituloEvento = (string)await cmdEv.ExecuteScalarAsync();

                    var cmdSub = new NpgsqlCommand(@"SELECT ""Id_Subtipo"", ""Nombre"", ""Cupo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento"" = @id AND ""Activo"" = TRUE", conexion);
                    cmdSub.Parameters.AddWithValue("@id", modelo.IdEvento);
                    using (var r = await cmdSub.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int idSub = (int)r["Id_Subtipo"];
                            modelo.Subtipos.Add(new SubtipoEventoItem { Id_Subtipo = idSub, Nombre = r["Nombre"].ToString(), Cupo = (int)r["Cupo"] });
                            modelo.LugaresCreadosPorSubtipo[idSub] = 0;
                        }
                    }

                    // Lista de Espera 
                    string sqlNoAsignados = @"
        SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Edad"", b.""Genero"", b.""Id_Subtipo"", s.""Nombre"" as ""Subtipo"", j.""Puntaje"", b.""Es_Cortesia""
        FROM ""Eventos_B_Asistentes"" b
        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
        LEFT JOIN ""Eventos_J_Prioridad_Alojamiento"" j ON b.""Id_Asistente"" = j.""Id_Asistente""
        WHERE r.""Id_Evento"" = @id 
        AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE)
        AND NOT EXISTS (SELECT 1 FROM ""Eventos_I_Alojamiento_Asignaciones"" a WHERE a.""Id_Asistente"" = b.""Id_Asistente"")";

                    using (var cmdNa = new NpgsqlCommand(sqlNoAsignados, conexion))
                    {
                        cmdNa.Parameters.AddWithValue("@id", modelo.IdEvento);
                        using (var r = await cmdNa.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.NoAsignados.Add(new AsistenteAlojamiento
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Edad = (int)r["Edad"],
                                    Genero = r["Genero"].ToString(),
                                    IdSubtipo = r["Id_Subtipo"] as int?,
                                    NombreSubtipo = r["Subtipo"]?.ToString() ?? "General",
                                    ScoreJusticia = r["Puntaje"] != DBNull.Value ? Convert.ToDecimal(r["Puntaje"]) : 0m,
                                    EsCortesia = r["Es_Cortesia"] != DBNull.Value && (bool)r["Es_Cortesia"]
                                });
                            }
                        }
                    }

                    // Zonas y Cuartos (CON CAPACIDAD HÍBRIDA)
                    var cmdZonas = new NpgsqlCommand(@"SELECT ""Id_Zona"", ""Nombre"" FROM ""Eventos_F_Zonas"" WHERE ""Id_Evento"" = @id", conexion);
                    cmdZonas.Parameters.AddWithValue("@id", modelo.IdEvento);
                    using (var rZ = await cmdZonas.ExecuteReaderAsync())
                    {
                        while (await rZ.ReadAsync()) modelo.Zonas.Add(new ZonaAlojamiento { IdZona = (int)rZ[0], Nombre = rZ[1].ToString() });
                    }

                    foreach (var zona in modelo.Zonas)
                    {
                        zona.SubtiposPermitidos = new List<int>();
                        using (var cmdZSub = new NpgsqlCommand(@"SELECT ""Id_Subtipo"" FROM ""Eventos_F_Zonas_Subtipos"" WHERE ""Id_Zona"" = @z", conexion))
                        {
                            cmdZSub.Parameters.AddWithValue("@z", zona.IdZona);
                            using (var rS = await cmdZSub.ExecuteReaderAsync())
                            {
                                while (await rS.ReadAsync()) zona.SubtiposPermitidos.Add((int)rS[0]);
                            }
                        }

                        var cmdAloj = new NpgsqlCommand(@"
                    SELECT a.""Id_Alojamiento"", a.""Nombre"", a.""Genero_Asignado"", 
                           (SELECT COUNT(*) FROM ""Eventos_K_Alojamientos_Publicados"" p WHERE p.""Id_Alojamiento"" = a.""Id_Alojamiento"" AND p.""Activo"" = TRUE) as ""Publicado""
                    FROM ""Eventos_G_Alojamientos"" a 
                    WHERE a.""Id_Zona"" = @z ORDER BY a.""Nombre"" ASC", conexion);

                        cmdAloj.Parameters.AddWithValue("@z", zona.IdZona);
                        using (var rA = await cmdAloj.ExecuteReaderAsync())
                        {
                            while (await rA.ReadAsync())
                            {
                                zona.Alojamientos.Add(new AlojamientoItem
                                {
                                    IdAlojamiento = (int)rA["Id_Alojamiento"],
                                    Nombre = rA["Nombre"].ToString(),
                                    GeneroAsignado = rA["Genero_Asignado"]?.ToString(),
                                    PublicadoPortal = Convert.ToInt32(rA["Publicado"]) > 0
                                });
                            }
                        }

                        foreach (var aloj in zona.Alojamientos)
                        {
                            // LEER CAPACIDADES HÍBRIDAS
                            var cmdCap = new NpgsqlCommand(@"
                SELECT c.""Id_Capacidad"", c.""Id_Subtipo"", c.""Id_Agrupacion"", c.""Capacidad_Total"", s.""Nombre"" as ""SubNombre"", ag.""Nombre_Descriptivo"" as ""AgNombre""
                FROM ""Eventos_H_Alojamiento_Capacidades"" c
                LEFT JOIN ""Eventos_Subtipos"" s ON c.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_K_AgrupaModalidades"" ag ON c.""Id_Agrupacion"" = ag.""Id_Agrupacion""
                WHERE c.""Id_Alojamiento"" = @a", conexion);
                            cmdCap.Parameters.AddWithValue("@a", aloj.IdAlojamiento);
                            using (var rC = await cmdCap.ExecuteReaderAsync())
                            {
                                while (await rC.ReadAsync())
                                {
                                    int capTotal = (int)rC["Capacidad_Total"];

                                    aloj.Capacidades.Add(new CapacidadAlojamiento
                                    {
                                        IdCapacidad = (int)rC["Id_Capacidad"],
                                        IdSubtipo = rC["Id_Subtipo"] != DBNull.Value ? (int)rC["Id_Subtipo"] : 0,
                                        IdAgrupacion = rC["Id_Agrupacion"] != DBNull.Value ? (int)rC["Id_Agrupacion"] : 0,
                                        CapacidadTotal = capTotal,
                                        NombreSubtipo = rC["SubNombre"]?.ToString(),
                                        NombreAgrupacion = rC["AgNombre"]?.ToString()
                                    });
                                }
                            }

                            // Ocupantes 
                            var cmdOcu = new NpgsqlCommand(@"
                SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Genero"", b.""Id_Subtipo"", j.""Puntaje"", b.""Es_Cortesia""
                FROM ""Eventos_I_Alojamiento_Asignaciones"" a
                JOIN ""Eventos_B_Asistentes"" b ON a.""Id_Asistente"" = b.""Id_Asistente""
                LEFT JOIN ""Eventos_J_Prioridad_Alojamiento"" j ON b.""Id_Asistente"" = j.""Id_Asistente""
                WHERE a.""Id_Alojamiento"" = @a", conexion);
                            cmdOcu.Parameters.AddWithValue("@a", aloj.IdAlojamiento);
                            using (var rO = await cmdOcu.ExecuteReaderAsync())
                            {
                                while (await rO.ReadAsync())
                                {
                                    int idSub = rO["Id_Subtipo"] != DBNull.Value ? (int)rO["Id_Subtipo"] : 0;

                                    // Buscar a dónde asignarlo visualmente (Modalidad directa o Agrupación)
                                    var cap = aloj.Capacidades.FirstOrDefault(c =>
                                        (c.IdSubtipo == idSub) ||
                                        (c.IdAgrupacion > 0 && modelo.Agrupaciones.FirstOrDefault(ag => ag.Id_Agrupacion == c.IdAgrupacion)?.NombresSubtipos.Contains(modelo.Subtipos.FirstOrDefault(s => s.Id_Subtipo == idSub)?.Nombre) == true)
                                    );

                                    if (cap != null)
                                    {
                                        cap.Ocupantes.Add(new AsistenteAlojamiento
                                        {
                                            IdAsistente = (int)rO["Id_Asistente"],
                                            Nombre = rO["Nombre_Completo"].ToString(),
                                            Genero = rO["Genero"].ToString(),
                                            IdSubtipo = idSub,
                                            ScoreJusticia = rO["Puntaje"] != DBNull.Value ? Convert.ToDecimal(rO["Puntaje"]) : 0m,
                                            EsCortesia = rO["Es_Cortesia"] != DBNull.Value && (bool)rO["Es_Cortesia"]
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(modelo);
        }
        /// <summary>
        /// AJAX: Busca al grupo de amigos/familiares (asistentes registrados por la misma cuenta) de un asistente en particular.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ApiBuscarGrupoAsistente(int idAsistente, int idEvento)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Json(new { exito = false, mensaje = "No tienes permisos de edición." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Encontrar al Usuario (Líder) y la Fecha de Registro
                    string sqlUsr = @"
                SELECT r.""Id_Usuario"", u.""NombreCompleto"", r.""Fecha_Registro""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                WHERE b.""Id_Asistente"" = @idA LIMIT 1";

                    int idRegistrador = 0;
                    string nombreLider = "";
                    string fechaRegistro = "";

                    using (var cmdU = new NpgsqlCommand(sqlUsr, conexion))
                    {
                        cmdU.Parameters.AddWithValue("@idA", idAsistente);
                        using (var reader = await cmdU.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                idRegistrador = (int)reader["Id_Usuario"];
                                nombreLider = reader["NombreCompleto"].ToString();
                                fechaRegistro = Convert.ToDateTime(reader["Fecha_Registro"]).ToString("dd/MMM/yyyy HH:mm");
                            }
                        }
                    }

                    if (idRegistrador == 0)
                        return Json(new { exito = false, mensaje = "No se encontró el origen del registro." });

                    // 2. Obtener a todo el grupo de ese Registrador
                    string sqlGrupo = @"
                SELECT 
                    b.""Id_Asistente"", b.""Nombre_Completo"", b.""Genero"", b.""Es_Cortesia"",
                    COALESCE(s.""Nombre"", 'General') as ""Modalidad"",
                    g.""Nombre"" as ""Cuarto"",
                    z.""Nombre"" as ""Zona""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_I_Alojamiento_Asignaciones"" a ON b.""Id_Asistente"" = a.""Id_Asistente""
                LEFT JOIN ""Eventos_G_Alojamientos"" g ON a.""Id_Alojamiento"" = g.""Id_Alojamiento""
                LEFT JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                WHERE r.""Id_Evento"" = @idEv AND r.""Id_Usuario"" = @idReg
                ORDER BY b.""Nombre_Completo"" ASC";

                    var companeros = new List<dynamic>();
                    using (var cmdG = new NpgsqlCommand(sqlGrupo, conexion))
                    {
                        cmdG.Parameters.AddWithValue("@idEv", idEvento);
                        cmdG.Parameters.AddWithValue("@idReg", idRegistrador);
                        using (var r = await cmdG.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string estatusAlojamiento = r["Cuarto"] != DBNull.Value
                                    ? $"{r["Zona"]} - {r["Cuarto"]}"
                                    : "POR ASIGNAR";

                                companeros.Add(new
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Genero = r["Genero"].ToString(),
                                    Modalidad = r["Modalidad"].ToString(),
                                    Alojamiento = estatusAlojamiento,
                                    EstaAsignado = r["Cuarto"] != DBNull.Value,
                                    EsCortesia = r["Es_Cortesia"] != DBNull.Value && (bool)r["Es_Cortesia"]
                                });
                            }
                        }
                    }

                    return Json(new
                    {
                        exito = true,
                        companeros = companeros,
                        lider = nombreLider,
                        fecha = fechaRegistro
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        /// <summary>
        /// Cambia la modalidad de un asistente. Puede respetar el costo original o aplicar reglas financieras (Deuda/Devolución).
        /// Además, retira cualquier asignación previa de alojamiento.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CambiarModalidadAsistente(int idAsistente, int idNuevoSubtipo, bool respetarCosto, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                //Regresar un mensaje de error a la vista de Inscritos 
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Inscritos", new { sid = sid });

            }

            int idUserAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            int idEvento = Funciones.DesencriptarId(sid);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "Por logística, el evento se encuentra bloqueado y ya no permite modificaciones a los registros.", TipoMensaje.Error);
                        return RedirectToAction("Registro", new { sid = sid });
                    }
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Consulta con bloqueo para evitar colisiones con pagos en curso
                            string sqlAsistente = @"
                        SELECT b.""Id_Subtipo"", b.""Nombre_Completo"", r.""Id_Evento""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        WHERE b.""Id_Asistente"" = @idA FOR UPDATE OF b";

                            int? idSubtipoActual = null;
                            string nombreAsistente = "";

                            using (var cmdA = new NpgsqlCommand(sqlAsistente, conexion, trans))
                            {
                                cmdA.Parameters.AddWithValue("@idA", idAsistente);
                                using (var r = await cmdA.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        if ((int)r["Id_Evento"] != idEvento) throw new Exception("El asistente no pertenece a este evento.");
                                        idSubtipoActual = r["Id_Subtipo"] as int?;
                                        nombreAsistente = r["Nombre_Completo"].ToString();
                                    }
                                    else throw new Exception("Asistente no encontrado.");
                                }
                            }

                            if (idSubtipoActual.HasValue && idSubtipoActual.Value == idNuevoSubtipo)
                            {
                                MostrarMensaje("Aviso", "El usuario ya se encuentra en esa modalidad.", TipoMensaje.Info);
                                return RedirectToAction("Inscritos", new { sid = sid });
                            }

                            // 2. Bloquear si tiene pagos en tránsito (Transferencias bloquean indefinidamente, Stripe caduca a los 30 mins)
                            string sqlTransito = @"
                            SELECT COUNT(*) FROM ""Eventos_D_Transacciones"" d 
                            JOIN ""Eventos_E_Pagos_Aplicados"" e ON d.""Id_Transaccion"" = e.""Id_Transaccion"" 
                            JOIN ""Eventos_C_Cuentas_Cobrar"" c ON e.""Id_Cuenta"" = c.""Id_Cuenta"" 
                            WHERE c.""Id_Asistente"" = @idA 
                            AND (
                                d.""Estatus_Pago"" IN ('waiting_proof', 'review') 
                                OR (d.""Estatus_Pago"" = 'pending' AND d.""Fecha_Intento"" >= (NOW() - INTERVAL '30 minutes'))
                            )";

                            using (var cmdTr = new NpgsqlCommand(sqlTransito, conexion, trans))
                            {
                                cmdTr.Parameters.AddWithValue("@idA", idAsistente);
                                if ((long)await cmdTr.ExecuteScalarAsync() > 0)
                                    throw new Exception("El asistente tiene un pago en proceso o revisión (o un intento reciente). Espera 30 minutos o dictamínalo antes de cambiar su modalidad.");
                            }

                            // 3. OBTENER INFORMACIÓN DE LA NUEVA MODALIDAD
                            var disp = await ConsultarDisponibilidadEvento(idEvento, conexion, trans, true, new List<int> { idAsistente });

                            if (disp == null) throw new Exception("Error al consultar el evento.");

                            var nuevaModalidad = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == idNuevoSubtipo);
                            if (nuevaModalidad == null) throw new Exception("La modalidad seleccionada no existe o no está activa.");

                            if (nuevaModalidad.LugaresDisponibles <= 0)
                                throw new Exception($"La modalidad '{nuevaModalidad.NombreModalidad}' ya está llena.");

                            decimal costoNuevo = nuevaModalidad.Costo;
                            string nombreNuevaModalidad = nuevaModalidad.NombreModalidad;

                            // 3.5 VALIDAR COMPATIBILIDAD DE EXTRAS
                            string sqlExtrasAsistente = @"
                                SELECT ape.""Id_ProductoExtra"", cpe.""Nombre_Producto"", cpe.""Id_Subtipo""
                                FROM ""Eventos_Asistentes_ProductosExtra"" ape
                                JOIN ""Eventos_Catalogo_ProductosExtra"" cpe ON ape.""Id_ProductoExtra"" = cpe.""Id_ProductoExtra""
                                WHERE ape.""Id_Asistente"" = @idA AND ape.""Cantidad"" > 0";
                            
                            using (var cmdExt = new NpgsqlCommand(sqlExtrasAsistente, conexion, trans))
                            {
                                cmdExt.Parameters.AddWithValue("@idA", idAsistente);
                                using (var rExt = await cmdExt.ExecuteReaderAsync())
                                {
                                    while (await rExt.ReadAsync())
                                    {
                                        int? idSub = rExt["Id_Subtipo"] as int?;
                                        if (idSub.HasValue && idSub.Value != idNuevoSubtipo)
                                        {
                                            throw new Exception($"No se puede cambiar la modalidad porque el asistente tiene el adicional '{rExt["Nombre_Producto"]}', el cual es exclusivo de su modalidad actual y no está disponible para la nueva.");
                                        }
                                    }
                                }
                            }

                            // 4. EJECUTAR EL CAMBIO DE MODALIDAD FÍSICO
                            await new NpgsqlCommand($"UPDATE \"Eventos_B_Asistentes\" SET \"Id_Subtipo\" = {idNuevoSubtipo} WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            string mensajeBitacora = $"Cambió de modalidad a '{nombreNuevaModalidad}'. ";

                            // -------------------------------------------------------------------------
                            // 4.5. REMOVER ASIGNACIÓN DE ALOJAMIENTO (Regla de negocio solicitada)
                            // -------------------------------------------------------------------------
                            string sqlGetAloj = @"SELECT ""Id_Alojamiento"" FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Asistente"" = @idA";
                            int? idAlojamientoAnterior = null;
                            using (var cmdAloj = new NpgsqlCommand(sqlGetAloj, conexion, trans))
                            {
                                cmdAloj.Parameters.AddWithValue("@idA", idAsistente);
                                var resAloj = await cmdAloj.ExecuteScalarAsync();
                                if (resAloj != null && resAloj != DBNull.Value) idAlojamientoAnterior = (int)resAloj;
                            }

                            if (idAlojamientoAnterior.HasValue)
                            {
                                // Borramos la asignación de este usuario
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_I_Alojamiento_Asignaciones\" WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                                // Verificamos si la habitación quedó vacía para liberar el candado de género
                                long quedanEnCuarto = (long)await new NpgsqlCommand($"SELECT COUNT(*) FROM \"Eventos_I_Alojamiento_Asignaciones\" WHERE \"Id_Alojamiento\" = {idAlojamientoAnterior.Value}", conexion, trans).ExecuteScalarAsync();
                                if (quedanEnCuarto == 0)
                                {
                                    await new NpgsqlCommand($"UPDATE \"Eventos_G_Alojamientos\" SET \"Genero_Asignado\" = NULL WHERE \"Id_Alojamiento\" = {idAlojamientoAnterior.Value}", conexion, trans).ExecuteNonQueryAsync();
                                }

                                mensajeBitacora += "Se liberó su alojamiento. ";
                            }
                            // -------------------------------------------------------------------------

                            // 5. LÓGICA FINANCIERA CON EL CALENDARIO OCULTO
                            if (respetarCosto)
                            {
                                mensajeBitacora += "Se respetó el costo original (Sin ajustes financieros).";
                            }
                            else
                            {
                                // --- CANCELAR DEVOLUCIONES PENDIENTES ---
                                string sqlLimpiarDevolucion = @"DELETE FROM ""Devoluciones"" 
                                    WHERE ""Modulo_Origen"" = 'Eventos' 
                                    AND ""Id_Referencia_Origen"" = @idA 
                                    AND ""Entregado"" = FALSE";

                                using (var cmdDelDev = new NpgsqlCommand(sqlLimpiarDevolucion, conexion, trans))
                                {
                                    cmdDelDev.Parameters.AddWithValue("@idA", idAsistente);
                                    int eliminadas = await cmdDelDev.ExecuteNonQueryAsync();
                                    if (eliminadas > 0) mensajeBitacora += "(Se canceló devolución previa no entregada). ";
                                }

                                decimal totalPagadoFirme = 0;
                                // Calcula el pago real descontando cupones Y restando el dinero que ya se le devolvió físicamente al usuario.
                                // FILTRADO ESTRICTO: Solo tomamos en cuenta pagos de MODALIDAD (cal.Numero_Pago > 0 o -1) para no distorsionar con productos extra.
                                string sqlPagosReales = @"
                                SELECT 
                                    (SELECT COALESCE(SUM(
                                        c.""Monto_Pagar"" - 
                                        COALESCE((
                                            SELECT (c.""Monto_Pagar"" / NULLIF((
                                                SELECT SUM(cx.""Monto_Pagar"") 
                                                FROM ""Eventos_E_Pagos_Aplicados"" ex 
                                                JOIN ""Eventos_C_Cuentas_Cobrar"" cx ON ex.""Id_Cuenta"" = cx.""Id_Cuenta"" 
                                                WHERE ex.""Id_Transaccion"" = t.""Id_Transaccion""
                                            ), 0)) * t.""Monto_Descuento""
                                            FROM ""Eventos_E_Pagos_Aplicados"" e
                                            JOIN ""Eventos_D_Transacciones"" t ON e.""Id_Transaccion"" = t.""Id_Transaccion""
                                            WHERE e.""Id_Cuenta"" = c.""Id_Cuenta"" AND t.""Completado"" = TRUE
                                            LIMIT 1
                                        ), 0)
                                    ), 0) 
                                    FROM ""Eventos_C_Cuentas_Cobrar"" c 
                                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                                    WHERE c.""Id_Asistente"" = @idA AND c.""Pagado"" = TRUE AND (cal.""Numero_Pago"" > 0 OR cal.""Numero_Pago"" = -1))
                                    - 
                                    (SELECT COALESCE(SUM(dp.""Monto_Pagado""), 0) 
                                     FROM ""Devoluciones_Pagos"" dp 
                                     JOIN ""Devoluciones"" d ON dp.""Id_Devolucion"" = d.""Id_Devolucion"" 
                                     WHERE d.""Modulo_Origen"" = 'Eventos' AND d.""Id_Referencia_Origen"" = @idA)";

                                using (var cmdPag = new NpgsqlCommand(sqlPagosReales, conexion, trans))
                                {
                                    cmdPag.Parameters.AddWithValue("@idA", idAsistente);
                                    totalPagadoFirme = (decimal)await cmdPag.ExecuteScalarAsync();
                                    if (totalPagadoFirme < 0) totalPagadoFirme = 0;
                                }

                                // 1. Expirar transacciones abandonadas en D y limpiar puentes E para evitar violación de Foreign Key al borrar cuentas de modalidad
                                string sqlExpModD = @"
                                UPDATE ""Eventos_D_Transacciones"" 
                                SET ""Estatus_Pago"" = 'expired', ""Completado"" = TRUE
                                WHERE ""Estatus_Pago"" = 'pending' 
                                AND ""Id_Transaccion"" IN (
                                    SELECT ""Id_Transaccion"" FROM ""Eventos_E_Pagos_Aplicados"" 
                                    WHERE ""Id_Cuenta"" IN (
                                        SELECT c.""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" c
                                        JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                                        WHERE c.""Id_Asistente"" = @idA AND c.""Pagado"" = FALSE AND (cal.""Numero_Pago"" > 0 OR cal.""Numero_Pago"" = -1)
                                    )
                                )";
                                using (var cmdExpD = new NpgsqlCommand(sqlExpModD, conexion, trans))
                                {
                                    cmdExpD.Parameters.AddWithValue("@idA", idAsistente);
                                    await cmdExpD.ExecuteNonQueryAsync();
                                }

                                string sqlDelModE = @"DELETE FROM ""Eventos_E_Pagos_Aplicados"" 
                                WHERE ""Id_Cuenta"" IN (
                                    SELECT c.""Id_Cuenta"" FROM ""Eventos_C_Cuentas_Cobrar"" c
                                    JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                                    WHERE c.""Id_Asistente"" = @idA AND c.""Pagado"" = FALSE AND (cal.""Numero_Pago"" > 0 OR cal.""Numero_Pago"" = -1)
                                )";
                                using (var cmdDelE = new NpgsqlCommand(sqlDelModE, conexion, trans))
                                {
                                    cmdDelE.Parameters.AddWithValue("@idA", idAsistente);
                                    await cmdDelE.ExecuteNonQueryAsync();
                                }

                                // 2. Limpiar únicamente la deuda no pagada de MODALIDAD (Protegiendo productos extra Numero_Pago <= -10000)
                                string sqlDelCuentasMod = @"DELETE FROM ""Eventos_C_Cuentas_Cobrar"" 
                                WHERE ""Id_Asistente"" = @idA 
                                  AND ""Pagado"" = FALSE 
                                  AND (""Id_Calendario"" IS NULL OR ""Id_Calendario"" IN (SELECT ""Id_Calendario"" FROM ""Eventos_Calendario"" WHERE ""Numero_Pago"" > 0 OR ""Numero_Pago"" = -1))";
                                using (var cmdDelMod = new NpgsqlCommand(sqlDelCuentasMod, conexion, trans))
                                {
                                    cmdDelMod.Parameters.AddWithValue("@idA", idAsistente);
                                    await cmdDelMod.ExecuteNonQueryAsync();
                                }

                                if (totalPagadoFirme < costoNuevo)
                                {
                                    // CASO A: EL NUEVO COSTO ES MAYOR (Genera Deuda en Calendario -1)
                                    decimal nuevoSaldoPendiente = costoNuevo - totalPagadoFirme;

                                    // 5.1 Buscar el Id del Calendario Oculto (Numero_Pago = -1)
                                    int idCalendarioOculto = 0;
                                    using (var cmdCal = new NpgsqlCommand(@"SELECT ""Id_Calendario"" FROM ""Eventos_Calendario"" WHERE ""Id_Evento"" = @ev AND ""Numero_Pago"" = -1 LIMIT 1", conexion, trans))
                                    {
                                        cmdCal.Parameters.AddWithValue("@ev", idEvento);
                                        var resCal = await cmdCal.ExecuteScalarAsync();

                                        if (resCal != null)
                                        {
                                            idCalendarioOculto = (int)resCal;
                                        }
                                        else
                                        {
                                            // Fallback extremo por si aún no han editado el evento y no se generó la modalidad auxiliar
                                            using (var cmdInsCal = new NpgsqlCommand(@"INSERT INTO ""Eventos_Calendario"" (""Id_Evento"", ""Numero_Pago"", ""Fecha_Limite"", ""Monto_Base"", ""Nombre_Concepto"", ""Id_Subtipo"") VALUES (@ev, -1, NOW() + INTERVAL '7 days', 0, 'Ajuste de Modalidad', NULL) RETURNING ""Id_Calendario""", conexion, trans))
                                            {
                                                cmdInsCal.Parameters.AddWithValue("@ev", idEvento);
                                                idCalendarioOculto = (int)await cmdInsCal.ExecuteScalarAsync();
                                            }
                                        }
                                    }

                                    string sqlNuevaDeuda = @"
                                        INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"") 
                                        VALUES (@idA, @idCal, @monto, FALSE)";

                                    using (var cmdIns = new NpgsqlCommand(sqlNuevaDeuda, conexion, trans))
                                    {
                                        cmdIns.Parameters.AddWithValue("@idA", idAsistente);
                                        cmdIns.Parameters.AddWithValue("@idCal", idCalendarioOculto);
                                        cmdIns.Parameters.AddWithValue("@monto", nuevoSaldoPendiente);
                                        await cmdIns.ExecuteNonQueryAsync();
                                    }

                                    await new NpgsqlCommand($"UPDATE \"Eventos_B_Asistentes\" SET \"Es_Pagado\" = FALSE WHERE \"Id_Asistente\" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();
                                    mensajeBitacora += $"Se generó cargo pendiente por ${nuevoSaldoPendiente:N2}.";
                                }
                                else if (totalPagadoFirme > costoNuevo)
                                {
                                    // CASO B: EL NUEVO COSTO ES MENOR (Genera Devolución)
                                    decimal saldoAFavor = totalPagadoFirme - costoNuevo;

                                    string sqlDevolucion = @"INSERT INTO ""Devoluciones"" (""Modulo_Origen"", ""Id_Referencia_Origen"", ""Monto"", ""Motivo"", ""Id_Usuario_Genera"") VALUES ('Eventos', @idA, @monto, @motivo, @usr)";

                                    using (var cmdDev = new NpgsqlCommand(sqlDevolucion, conexion, trans))
                                    {
                                        cmdDev.Parameters.AddWithValue("@idA", idAsistente);
                                        cmdDev.Parameters.AddWithValue("@monto", saldoAFavor);
                                        cmdDev.Parameters.AddWithValue("@motivo", $"Diferencia a favor por cambio de modalidad a: {nombreNuevaModalidad}");
                                        cmdDev.Parameters.AddWithValue("@usr", idUserAdmin);
                                        await cmdDev.ExecuteNonQueryAsync();
                                    }

                                    string sqlUpdEsPagado = @"UPDATE ""Eventos_B_Asistentes"" 
                                    SET ""Es_Pagado"" = (NOT EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA AND ""Pagado"" = FALSE)) 
                                    WHERE ""Id_Asistente"" = @idA";
                                    using (var cmdUpdPag = new NpgsqlCommand(sqlUpdEsPagado, conexion, trans))
                                    {
                                        cmdUpdPag.Parameters.AddWithValue("@idA", idAsistente);
                                        await cmdUpdPag.ExecuteNonQueryAsync();
                                    }
                                    mensajeBitacora += $"Se generó orden de devolución por ${saldoAFavor:N2}.";
                                }
                                else
                                {
                                    // CASO C: CUADRA EXACTO
                                    string sqlUpdEsPagado = @"UPDATE ""Eventos_B_Asistentes"" 
                                    SET ""Es_Pagado"" = (NOT EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA AND ""Pagado"" = FALSE)) 
                                    WHERE ""Id_Asistente"" = @idA";
                                    using (var cmdUpdPag = new NpgsqlCommand(sqlUpdEsPagado, conexion, trans))
                                    {
                                        cmdUpdPag.Parameters.AddWithValue("@idA", idAsistente);
                                        await cmdUpdPag.ExecuteNonQueryAsync();
                                    }
                                    mensajeBitacora += "El costo cuadró exacto. No hay saldo pendiente.";
                                }
                            }

                            // 6. BITÁCORA Y COMMIT
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUserAdmin, Modulo, Parametros.AccionesBitacora.Editar, $"Modificó modalidad de {nombreAsistente}. " + mensajeBitacora, ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Cambio Exitoso", mensajeBitacora, TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw ex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Inscritos", new { sid = sid });
        }

        /// <summary>
        /// AJAX: Recalcula y actualiza la tabla J de Prioridades (Scores) basándose en los pagos reales del líder del grupo.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiActualizarPuntajesAlojamientos(int idEvento)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Json(new { exito = false, mensaje = "No tienes permisos de edición." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var listaRaw = new List<AsistenteRaw>();
                    string sqlRaw = @"
                SELECT 
                    b.""Id_Asistente"", 
                    r.""Id_Usuario"",
                    (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as ""PagosHechos"",
                    (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as ""TotalPagos""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                WHERE r.""Id_Evento"" = @id";

                    using (var cmdRaw = new NpgsqlCommand(sqlRaw, conexion))
                    {
                        cmdRaw.Parameters.AddWithValue("@id", idEvento);
                        using (var reader = await cmdRaw.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                listaRaw.Add(new AsistenteRaw
                                {
                                    IdAsistente = (int)reader["Id_Asistente"],
                                    IdRegistrador = (int)reader["Id_Usuario"],
                                    PagosHechos = Convert.ToInt32(reader["PagosHechos"]),
                                    TotalPagos = Convert.ToInt32(reader["TotalPagos"])
                                });
                            }
                        }
                    }

                    // Calcular puntaje por inscriptor (Líder)
                    var statsPorLider = listaRaw.GroupBy(x => x.IdRegistrador).ToDictionary(
                        g => g.Key,
                        g => {
                            decimal totalReq = g.Sum(x => x.TotalPagos);
                            decimal totalHechos = g.Sum(x => x.PagosHechos);
                            return totalReq > 0 ? (totalHechos / totalReq) * totalHechos : 0m;
                        });

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Limpiar la caché vieja de este evento
                        await new NpgsqlCommand($"DELETE FROM \"Eventos_J_Prioridad_Alojamiento\" WHERE \"Id_Evento\" = {idEvento}", conexion, trans).ExecuteNonQueryAsync();

                        // Guardar los nuevos puntajes
                        string sqlInsertJ = @"INSERT INTO ""Eventos_J_Prioridad_Alojamiento"" (""Id_Evento"", ""Id_Asistente"", ""Puntaje"") VALUES (@ev, @asis, @pts)";
                        foreach (var asis in listaRaw)
                        {
                            decimal scoreCalculado = statsPorLider.ContainsKey(asis.IdRegistrador) ? statsPorLider[asis.IdRegistrador] : 0m;
                            using (var cmdIns = new NpgsqlCommand(sqlInsertJ, conexion, trans))
                            {
                                cmdIns.Parameters.AddWithValue("@ev", idEvento);
                                cmdIns.Parameters.AddWithValue("@asis", asis.IdAsistente);
                                cmdIns.Parameters.AddWithValue("@pts", scoreCalculado);
                                await cmdIns.ExecuteNonQueryAsync();
                            }
                        }
                        await trans.CommitAsync();
                    }

                    return Json(new { exito = true, mensaje = "¡Las prioridades se han recalculado correctamente en base a los pagos recientes!" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }
        /// <summary>
        /// AJAX: Algoritmo de Acomodo Automático Inteligente.
        /// Lee la prioridad directamente de la tabla J (Caché de puntajes).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiAcomodoAutomatico([FromBody] AutoAcomodoRequest req)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Json(new { exito = false, mensaje = "No tienes permisos de edición." });

            int asignados = 0;
            bool filtroAplicado = req.IdsSeleccionados != null && req.IdsSeleccionados.Any();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // =================================================================================
                    // FASE 0: MAPEO DE AGRUPACIONES
                    // =================================================================================
                    var mapaSubtipoAgrupacion = new Dictionary<int, int>();
                    string sqlMapa = @"SELECT ""Id_Subtipo"", ""Id_Agrupacion"" FROM ""Eventos_K_AgrupaModalidadesDetalle""";
                    using (var cmdM = new NpgsqlCommand(sqlMapa, conexion))
                    using (var rM = await cmdM.ExecuteReaderAsync())
                        while (await rM.ReadAsync()) mapaSubtipoAgrupacion[(int)rM[0]] = (int)rM[1];

                    // =================================================================================
                    // FASE 1: OBTENER ASISTENTES PENDIENTES
                    // =================================================================================
                    var pendientes = new List<AsistentePendiente>();
                    string sqlPendientes = @"
                SELECT b.""Id_Asistente"", b.""Genero"", COALESCE(b.""Id_Subtipo"", 0) as ""Id_Subtipo"", 
                       r.""Id_Usuario"", j.""Puntaje""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                LEFT JOIN ""Eventos_J_Prioridad_Alojamiento"" j ON b.""Id_Asistente"" = j.""Id_Asistente""
                WHERE r.""Id_Evento"" = @id
                AND (@tieneFiltro = FALSE OR b.""Id_Asistente"" = ANY(@idsSeleccionados))
                AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE)
                AND NOT EXISTS (SELECT 1 FROM ""Eventos_I_Alojamiento_Asignaciones"" a WHERE a.""Id_Asistente"" = b.""Id_Asistente"")";

                    using (var cmd = new NpgsqlCommand(sqlPendientes, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", req.IdEvento);
                        cmd.Parameters.AddWithValue("@tieneFiltro", filtroAplicado);
                        cmd.Parameters.AddWithValue("@idsSeleccionados", filtroAplicado ? (object)req.IdsSeleccionados : DBNull.Value);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                                pendientes.Add(new AsistentePendiente
                                {
                                    IdAsistente = (int)reader["Id_Asistente"],
                                    Genero = reader["Genero"].ToString(),
                                    IdSubtipo = (int)reader["Id_Subtipo"],
                                    IdRegistrador = (int)reader["Id_Usuario"],
                                    ScoreJusticia = reader["Puntaje"] != DBNull.Value ? Convert.ToDecimal(reader["Puntaje"]) : 0m
                                });
                    }

                    if (!pendientes.Any()) return Json(new { exito = false, mensaje = "No hay personas compatibles en la lista de espera." });

                    var bloquesOrdenados = pendientes
                        .GroupBy(p => new { p.IdRegistrador, p.Genero, p.IdSubtipo, p.ScoreJusticia })
                        .Select(g => new BloqueAsignacion
                        {
                            IdRegistrador = g.Key.IdRegistrador,
                            Genero = g.Key.Genero,
                            IdSubtipo = g.Key.IdSubtipo,
                            ScoreJusticia = g.Key.ScoreJusticia,
                            Asistentes = g.ToList()
                        })
                        .OrderByDescending(b => b.Genero == "M").ThenByDescending(b => b.ScoreJusticia).ThenByDescending(b => b.Asistentes.Count).ToList();

                    // =================================================================================
                    // FASE 3: CARGAR MAPA DE CUARTOS
                    // =================================================================================
                    var cuartos = new Dictionary<int, CuartoInfoHibrido>();
                    string sqlCuartos = @"
                SELECT g.""Id_Alojamiento"", g.""Genero_Asignado"", c.""Id_Subtipo"", c.""Id_Agrupacion"", c.""Capacidad_Total""
                FROM ""Eventos_G_Alojamientos"" g
                JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                JOIN ""Eventos_H_Alojamiento_Capacidades"" c ON g.""Id_Alojamiento"" = c.""Id_Alojamiento""
                WHERE z.""Id_Evento"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlCuartos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", req.IdEvento);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                int idAloj = (int)reader["Id_Alojamiento"];
                                if (!cuartos.ContainsKey(idAloj))
                                    cuartos[idAloj] = new CuartoInfoHibrido
                                    {
                                        IdAlojamiento = idAloj,
                                        GeneroAsignado = reader["Genero_Asignado"]?.ToString() ?? "",
                                        Capacidades = new Dictionary<string, int>(),
                                        Ocupados = new Dictionary<string, int>(),
                                        RegistradoresEnCuarto = new HashSet<int>()
                                    };

                                int? idSub = reader["Id_Subtipo"] != DBNull.Value ? (int?)reader["Id_Subtipo"] : null;
                                int? idAgr = reader["Id_Agrupacion"] != DBNull.Value ? (int?)reader["Id_Agrupacion"] : null;

                                string key = idSub.HasValue ? $"SUB_{idSub}" : $"AGR_{idAgr}";
                                cuartos[idAloj].Capacidades[key] = (int)reader["Capacidad_Total"];
                                cuartos[idAloj].Ocupados[key] = 0;
                            }
                        }
                    }

                    // Cargar ocupación actual
                    string sqlOcupacion = @"
                SELECT a.""Id_Alojamiento"", b.""Id_Subtipo"", r.""Id_Usuario""
                FROM ""Eventos_I_Alojamiento_Asignaciones"" a
                JOIN ""Eventos_B_Asistentes"" b ON a.""Id_Asistente"" = b.""Id_Asistente""
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_G_Alojamientos"" g ON a.""Id_Alojamiento"" = g.""Id_Alojamiento""
                JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                WHERE z.""Id_Evento"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlOcupacion, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", req.IdEvento);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                int idAloj = (int)reader["Id_Alojamiento"];
                                int idSubAsis = (int)reader["Id_Subtipo"];
                                int idReg = (int)reader["Id_Usuario"];

                                if (cuartos.ContainsKey(idAloj))
                                {
                                    cuartos[idAloj].RegistradoresEnCuarto.Add(idReg);
                                    string keySub = $"SUB_{idSubAsis}";
                                    int? idAgrPertenece = mapaSubtipoAgrupacion.ContainsKey(idSubAsis) ? (int?)mapaSubtipoAgrupacion[idSubAsis] : null;
                                    string keyAgr = idAgrPertenece.HasValue ? $"AGR_{idAgrPertenece}" : "NONE";

                                    if (cuartos[idAloj].Ocupados.ContainsKey(keySub)) cuartos[idAloj].Ocupados[keySub]++;
                                    else if (cuartos[idAloj].Ocupados.ContainsKey(keyAgr)) cuartos[idAloj].Ocupados[keyAgr]++;
                                }
                            }
                        }
                    }

                    // =================================================================================
                    // FASE 4: ACOMODO INTELIGENTE
                    // =================================================================================
                    var asignacionesNuevas = new List<Tuple<int, int>>();
                    var cuartosConCambioDeGenero = new Dictionary<int, string>();

                    foreach (var bloque in bloquesOrdenados)
                    {
                        while (bloque.Asistentes.Count > 0)
                        {
                            int tamanioRestanteDelBloque = bloque.Asistentes.Count;
                            var persona = bloque.Asistentes.First();
                            CuartoInfoHibrido mejorCuarto = null;
                            string keyUsada = "";
                            int scoreMaximo = -int.MaxValue;

                            int? idAgrAsis = mapaSubtipoAgrupacion.ContainsKey(persona.IdSubtipo) ? (int?)mapaSubtipoAgrupacion[persona.IdSubtipo] : null;
                            string keyAgrAsis = idAgrAsis.HasValue ? $"AGR_{idAgrAsis}" : "NONE";
                            string keySubAsis = $"SUB_{persona.IdSubtipo}";

                            foreach (var c in cuartos.Values)
                            {
                                string keyCompatible = c.Capacidades.ContainsKey(keySubAsis) ? keySubAsis : (c.Capacidades.ContainsKey(keyAgrAsis) ? keyAgrAsis : null);
                                if (keyCompatible == null) continue;

                                int disponibles = c.Capacidades[keyCompatible] - c.Ocupados[keyCompatible];
                                if (disponibles <= 0) continue;

                                bool generoOk = string.IsNullOrEmpty(c.GeneroAsignado) || c.GeneroAsignado == persona.Genero || c.GeneroAsignado == "X";
                                if (!generoOk) continue;

                                int score = 0;
                                if (c.RegistradoresEnCuarto.Contains(persona.IdRegistrador)) score += 1000;
                                if (c.RegistradoresEnCuarto.Any()) score += 10;

                                // Restauración del cálculo heurístico
                                if (disponibles == tamanioRestanteDelBloque) score += 500;
                                else if (disponibles > tamanioRestanteDelBloque) score += 300;
                                else score -= 200;

                                score -= c.Capacidades[keyCompatible];

                                if (score > scoreMaximo)
                                {
                                    scoreMaximo = score;
                                    mejorCuarto = c;
                                    keyUsada = keyCompatible;
                                }
                            }

                            if (mejorCuarto != null)
                            {
                                asignacionesNuevas.Add(new Tuple<int, int>(mejorCuarto.IdAlojamiento, persona.IdAsistente));
                                mejorCuarto.Ocupados[keyUsada]++;
                                mejorCuarto.RegistradoresEnCuarto.Add(persona.IdRegistrador);
                                if (string.IsNullOrEmpty(mejorCuarto.GeneroAsignado))
                                {
                                    mejorCuarto.GeneroAsignado = persona.Genero;
                                    cuartosConCambioDeGenero[mejorCuarto.IdAlojamiento] = persona.Genero;
                                }
                                asignados++;
                                bloque.Asistentes.RemoveAt(0);
                            }
                            else break;
                        }
                    }

                    if (asignados > 0)
                    {
                        using (var trans = await conexion.BeginTransactionAsync())
                        {
                            foreach (var asig in asignacionesNuevas)
                            {
                                using (var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_I_Alojamiento_Asignaciones"" (""Id_Alojamiento"", ""Id_Asistente"") VALUES (@a, @usr)", conexion, trans))
                                { cmd.Parameters.AddWithValue("@a", asig.Item1); cmd.Parameters.AddWithValue("@usr", asig.Item2); await cmd.ExecuteNonQueryAsync(); }
                            }
                            foreach (var kvp in cuartosConCambioDeGenero)
                            {
                                using (var cmd = new NpgsqlCommand(@"UPDATE ""Eventos_G_Alojamientos"" SET ""Genero_Asignado"" = @g WHERE ""Id_Alojamiento"" = @a", conexion, trans))
                                { cmd.Parameters.AddWithValue("@g", kvp.Value); cmd.Parameters.AddWithValue("@a", kvp.Key); await cmd.ExecuteNonQueryAsync(); }
                            }
                            await trans.CommitAsync();
                        }

                        // --- SIGNALR NOTIFICATION ---
                        await _eventosHub.Clients.Group($"Evento_{req.IdEvento}").SendAsync("AlojamientoActualizado");

                        return Json(new { exito = true, mensaje = $"Acomodo automático: {asignados} personas asignadas." });
                    }

                    return Json(new { exito = false, mensaje = "No se encontraron espacios compatibles." });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiVaciarAlojamientosSeleccionados([FromBody] List<int> idsAlojamientos)
        {
            try
            {
                if (idsAlojamientos == null || !idsAlojamientos.Any())
                    return Json(new { exito = false, mensaje = "No se seleccionaron habitaciones." });

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Rescatar IdEvento para notificar por SignalR a todos los usuarios
                        int idEventoDetectado = 0;
                        string sqlGetEv = @"SELECT z.""Id_Evento"" FROM ""Eventos_G_Alojamientos"" g JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona"" WHERE g.""Id_Alojamiento"" = @id LIMIT 1";
                        using (var cmdE = new NpgsqlCommand(sqlGetEv, conexion, trans))
                        {
                            cmdE.Parameters.AddWithValue("@id", idsAlojamientos.First());
                            var resEv = await cmdE.ExecuteScalarAsync();
                            if (resEv != null) idEventoDetectado = (int)resEv;
                        }

                        // Eliminar únicamente las asignaciones de las personas en esas habitaciones
                        string sqlDel = @"DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Alojamiento"" = ANY(@ids)";
                        using (var cmdDel = new NpgsqlCommand(sqlDel, conexion, trans))
                        {
                            cmdDel.Parameters.AddWithValue("@ids", idsAlojamientos);
                            await cmdDel.ExecuteNonQueryAsync();
                        }

                        // NOTA: Intencionalmente NO hacemos el UPDATE a Genero_Asignado = NULL. 

                        await trans.CommitAsync();

                        // Notificar refresco de interfaz a usuarios conectados
                        if (idEventoDetectado > 0)
                        {
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                        }

                        return Json(new { exito = true, mensaje = $"Se han vaciado las {idsAlojamientos.Count} habitaciones seleccionadas." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiVaciarAsignacionesZona(int idZona, bool limpiarGeneros)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Rescatar IdEvento para notificar por SignalR
                        int idEventoDetectado = 0;
                        using (var cmdEv = new NpgsqlCommand(@"SELECT ""Id_Evento"" FROM ""Eventos_F_Zonas"" WHERE ""Id_Zona"" = @id", conexion, trans))
                        {
                            cmdEv.Parameters.AddWithValue("@id", idZona);
                            var resEv = await cmdEv.ExecuteScalarAsync();
                            if (resEv != null) idEventoDetectado = (int)resEv;
                        }

                        // 1. Eliminar asignaciones de los alojamientos de esta zona [cite: 310, 313]
                        var cmd = new NpgsqlCommand(@"
                    DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" 
                    WHERE ""Id_Alojamiento"" IN (
                        SELECT ""Id_Alojamiento"" 
                        FROM ""Eventos_G_Alojamientos"" 
                        WHERE ""Id_Zona"" = @id
                    )", conexion, trans);
                        cmd.Parameters.AddWithValue("@id", idZona);
                        await cmd.ExecuteNonQueryAsync();

                        // 2. Liberar el género de los alojamientos de esta zona SOLO si se solicita [cite: 310]
                        if (limpiarGeneros)
                        {
                            var cmdGen = new NpgsqlCommand(@"
                        UPDATE ""Eventos_G_Alojamientos"" 
                        SET ""Genero_Asignado"" = NULL 
                        WHERE ""Id_Zona"" = @id", conexion, trans);
                            cmdGen.Parameters.AddWithValue("@id", idZona);
                            await cmdGen.ExecuteNonQueryAsync();
                        }

                        await trans.CommitAsync();

                        // Notificación en tiempo real [cite: 310]
                        if (idEventoDetectado > 0)
                        {
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                        }

                        return Json(new { exito = true, mensaje = "La zona ha sido vaciada correctamente." });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Vacía todas las asignaciones de alojamientos del evento actual.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiVaciarAsignaciones(int idEvento)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. Eliminar todas las asignaciones del evento
                        var cmd = new NpgsqlCommand(@"
                            DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" 
                            WHERE ""Id_Alojamiento"" IN (
                                SELECT a.""Id_Alojamiento"" 
                                FROM ""Eventos_G_Alojamientos"" a
                                JOIN ""Eventos_F_Zonas"" z ON a.""Id_Zona"" = z.""Id_Zona""
                                WHERE z.""Id_Evento"" = @id
                            )", conexion, trans);
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        await cmd.ExecuteNonQueryAsync();

                        // 2. Liberar el género de todos los alojamientos
                        var cmdGen = new NpgsqlCommand(@"
                            UPDATE ""Eventos_G_Alojamientos"" 
                            SET ""Genero_Asignado"" = NULL 
                            WHERE ""Id_Zona"" IN (SELECT ""Id_Zona"" FROM ""Eventos_F_Zonas"" WHERE ""Id_Evento"" = @id)", conexion, trans);
                        cmdGen.Parameters.AddWithValue("@id", idEvento);
                        await cmdGen.ExecuteNonQueryAsync();

                        await trans.CommitAsync();

                        // --- SIGNALR NOTIFICATION ---
                        await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("AlojamientoActualizado");

                        return Json(new { exito = true, mensaje = "Todas las asignaciones han sido removidas." });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Elimina un alojamiento siempre y cuando esté vacío.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEliminarAlojamiento(int idAlojamiento)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Validar si la habitación tiene personas asignadas
                    var cmdCheck = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Alojamiento"" = @id", conexion);
                    cmdCheck.Parameters.AddWithValue("@id", idAlojamiento);
                    long ocupantes = (long)await cmdCheck.ExecuteScalarAsync();

                    if (ocupantes > 0)
                    {
                        return Json(new { exito = false, mensaje = "No puedes eliminar una habitación que tiene personas asignadas. Vacíala primero." });
                    }

                    // Borrado seguro en cascada manual
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. Eliminar capacidades (Tabla H)
                        var cmdDelCap = new NpgsqlCommand(@"DELETE FROM ""Eventos_H_Alojamiento_Capacidades"" WHERE ""Id_Alojamiento"" = @id", conexion, trans);
                        cmdDelCap.Parameters.AddWithValue("@id", idAlojamiento);
                        await cmdDelCap.ExecuteNonQueryAsync();

                        // 2. Eliminar el alojamiento (Tabla G)
                        var cmdDelAloj = new NpgsqlCommand(@"DELETE FROM ""Eventos_G_Alojamientos"" WHERE ""Id_Alojamiento"" = @id", conexion, trans);
                        cmdDelAloj.Parameters.AddWithValue("@id", idAlojamiento);
                        await cmdDelAloj.ExecuteNonQueryAsync();

                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEliminarAlojamientoMultiple([FromBody] List<int> idsAlojamientos)
        {
            if (idsAlojamientos == null || !idsAlojamientos.Any())
                return Json(new { exito = false, mensaje = "No se seleccionaron habitaciones." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Validar si ALGUNA de las habitaciones seleccionadas tiene personas asignadas
                    var nombresOcupados = new List<string>();
                    string sqlCheck = @"
                SELECT a.""Nombre""
                FROM ""Eventos_G_Alojamientos"" a
                WHERE a.""Id_Alojamiento"" = ANY(@ids)
                AND EXISTS (
                    SELECT 1 FROM ""Eventos_I_Alojamiento_Asignaciones"" asig 
                    WHERE asig.""Id_Alojamiento"" = a.""Id_Alojamiento""
                )";

                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@ids", idsAlojamientos);
                        using (var r = await cmdCheck.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                nombresOcupados.Add(r["Nombre"].ToString());
                            }
                        }
                    }

                    // Si hay habitaciones ocupadas, abortamos e informamos al usuario
                    if (nombresOcupados.Any())
                    {
                        string cuartos = string.Join(", ", nombresOcupados);
                        return Json(new { exito = false, mensaje = $"Operación cancelada: Las siguientes habitaciones tienen personas asignadas y deben vaciarse primero: {cuartos}" });
                    }

                    // 2. Si todas están vacías, procedemos con el borrado seguro en cascada
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Eliminar capacidades (Tabla H)
                        var cmdDelCap = new NpgsqlCommand(@"DELETE FROM ""Eventos_H_Alojamiento_Capacidades"" WHERE ""Id_Alojamiento"" = ANY(@ids)", conexion, trans);
                        cmdDelCap.Parameters.AddWithValue("@ids", idsAlojamientos);
                        await cmdDelCap.ExecuteNonQueryAsync();

                        // Eliminar los alojamientos (Tabla G)
                        var cmdDelAloj = new NpgsqlCommand(@"DELETE FROM ""Eventos_G_Alojamientos"" WHERE ""Id_Alojamiento"" = ANY(@ids)", conexion, trans);
                        cmdDelAloj.Parameters.AddWithValue("@ids", idsAlojamientos);
                        await cmdDelAloj.ExecuteNonQueryAsync();

                        await trans.CommitAsync();
                        return Json(new { exito = true, mensaje = $"{idsAlojamientos.Count} habitaciones eliminadas correctamente." });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// Genera un Excel con el diseño dual: Directorio de Recepción y Logística de Cabañas.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DescargarReporteAlojamientos(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);
            string tituloEvento = "";
            var listaGeneral = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Obtener Título
                    var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion);
                    cmdEv.Parameters.AddWithValue("@id", idEvento);
                    tituloEvento = (string)await cmdEv.ExecuteScalarAsync();

                    // 2. Consulta Maestra (Trae a todos los que tienen derecho a alojamiento)
                    string sql = @"
                        SELECT 
                            b.""Nombre_Completo"", b.""Genero"", b.""Edad"", b.""Telefono_Externo"",
                            COALESCE(s.""Nombre"", 'General') as ""Modalidad"",
                            z.""Nombre"" as ""Zona"",
                            a.""Nombre"" as ""Alojamiento"",
                            (CASE WHEN asig.""Id_Asignacion"" IS NOT NULL THEN TRUE ELSE FALSE END) as ""Asignado""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                        LEFT JOIN ""Eventos_I_Alojamiento_Asignaciones"" asig ON b.""Id_Asistente"" = asig.""Id_Asistente""
                        LEFT JOIN ""Eventos_G_Alojamientos"" a ON asig.""Id_Alojamiento"" = a.""Id_Alojamiento""
                        LEFT JOIN ""Eventos_F_Zonas"" z ON a.""Id_Zona"" = z.""Id_Zona""
                        WHERE r.""Id_Evento"" = @id
                        AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE)
                        ORDER BY z.""Id_Zona"" ASC, a.""Id_Alojamiento"" ASC, b.""Nombre_Completo"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                listaGeneral.Add(new
                                {
                                    Nombre = reader["Nombre_Completo"].ToString(),
                                    Genero = reader["Genero"].ToString() == "H" ? "Hombre" : "Mujer",
                                    Edad = (int)reader["Edad"],
                                    Telefono = reader["Telefono_Externo"]?.ToString() ?? "N/D",
                                    Modalidad = reader["Modalidad"].ToString(),
                                    Zona = reader["Zona"]?.ToString() ?? "SIN ASIGNAR",
                                    Alojamiento = reader["Alojamiento"]?.ToString() ?? "SIN ASIGNAR",
                                    Asignado = (bool)reader["Asignado"]
                                });
                            }
                        }
                    }
                }

                using (var wb = new ClosedXML.Excel.XLWorkbook())
                {
                    // =========================================================
                    // HOJA 1: DIRECTORIO DE RECEPCIÓN (Check-in)
                    // =========================================================
                    var wsRecepcion = wb.Worksheets.Add("Directorio Recepción");
                    wsRecepcion.Cell(1, 1).Value = "Estatus";
                    wsRecepcion.Cell(1, 2).Value = "Nombre Completo";
                    wsRecepcion.Cell(1, 3).Value = "Zona";
                    wsRecepcion.Cell(1, 4).Value = "Alojamiento (Cuarto)";
                    wsRecepcion.Cell(1, 5).Value = "Modalidad";
                    wsRecepcion.Cell(1, 6).Value = "Género";
                    wsRecepcion.Cell(1, 7).Value = "Edad";

                    // Estilo Cabecera
                    var rngHead1 = wsRecepcion.Range("A1:G1");
                    rngHead1.Style.Font.SetBold().Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                    rngHead1.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50"));
                    rngHead1.SetAutoFilter();
                    wsRecepcion.SheetView.FreezeRows(1); // Congelar fila 1

                    int f = 2;
                    // Ordenamos alfabéticamente para facilitar la búsqueda en recepción
                    foreach (var item in listaGeneral.OrderBy(x => x.Nombre))
                    {
                        wsRecepcion.Cell(f, 1).Value = item.Asignado ? "✅ ASIGNADO" : "❌ PENDIENTE";
                        wsRecepcion.Cell(f, 1).Style.Font.SetFontColor(item.Asignado ? ClosedXML.Excel.XLColor.Green : ClosedXML.Excel.XLColor.Red);

                        wsRecepcion.Cell(f, 2).Value = item.Nombre;
                        wsRecepcion.Cell(f, 3).Value = item.Zona;
                        wsRecepcion.Cell(f, 4).Value = item.Alojamiento;
                        wsRecepcion.Cell(f, 5).Value = item.Modalidad;
                        wsRecepcion.Cell(f, 6).Value = item.Genero;
                        wsRecepcion.Cell(f, 7).Value = item.Edad;
                        f++;
                    }
                    wsRecepcion.Columns().AdjustToContents();

                    // =========================================================
                    // HOJA 2: LOGÍSTICA DE CABAÑAS (Staff / Limpieza)
                    // =========================================================
                    var wsLogistica = wb.Worksheets.Add("Logística Habitaciones");
                    int filaL = 1;

                    // Agrupamos por Zona -> Alojamiento (Excluyendo a los no asignados)
                    var asignados = listaGeneral.Where(x => x.Asignado).GroupBy(x => x.Zona);

                    foreach (var zonaGroup in asignados)
                    {
                        // Bloque de Zona (Nivel 1)
                        wsLogistica.Range(filaL, 1, filaL, 4).Merge().Value = $"🏢 ZONA: {zonaGroup.Key.ToUpper()}";
                        wsLogistica.Range(filaL, 1, filaL, 4).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                        wsLogistica.Range(filaL, 1, filaL, 4).Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#34495e"));
                        wsLogistica.Range(filaL, 1, filaL, 4).Style.Alignment.SetVertical(ClosedXML.Excel.XLAlignmentVerticalValues.Center);
                        wsLogistica.Row(filaL).Height = 30;
                        filaL++;

                        var alojamientosGroup = zonaGroup.GroupBy(x => x.Alojamiento);
                        foreach (var alojGroup in alojamientosGroup)
                        {
                            // Bloque de Cabaña/Cuarto (Nivel 2)
                            wsLogistica.Range(filaL, 1, filaL, 4).Merge().Value = $"🔑 {alojGroup.Key.ToUpper()} ({alojGroup.Count()} Ocupantes)";
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50"));
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#ecf0f1"));
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Medium;
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Border.BottomBorderColor = ClosedXML.Excel.XLColor.FromHtml("#bdc3c7");
                            filaL++;

                            // Cabeceras de la tabla interna
                            wsLogistica.Cell(filaL, 1).Value = "Nombre del Asistente";
                            wsLogistica.Cell(filaL, 2).Value = "Modalidad";
                            wsLogistica.Cell(filaL, 3).Value = "Teléfono";
                            wsLogistica.Cell(filaL, 4).Value = "Género";
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Font.SetBold().Font.SetFontColor(ClosedXML.Excel.XLColor.FromHtml("#7f8c8d"));
                            wsLogistica.Range(filaL, 1, filaL, 4).Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                            filaL++;

                            // Ocupantes (Nivel 3)
                            foreach (var ocupante in alojGroup)
                            {
                                wsLogistica.Cell(filaL, 1).Value = ocupante.Nombre;
                                wsLogistica.Cell(filaL, 2).Value = ocupante.Modalidad;
                                wsLogistica.Cell(filaL, 3).Value = ocupante.Telefono;
                                wsLogistica.Cell(filaL, 4).Value = ocupante.Genero;
                                filaL++;
                            }
                            filaL++; // Espacio en blanco entre habitaciones para facilitar la lectura impresa
                        }
                        filaL++; // Espacio mayor entre Zonas
                    }

                    wsLogistica.Column(1).Width = 40;
                    wsLogistica.Column(2).Width = 25;
                    wsLogistica.Column(3).Width = 15;
                    wsLogistica.Column(4).Width = 15;

                    using (var stream = new MemoryStream())
                    {
                        wb.SaveAs(stream);
                        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Alojamientos_{tituloEvento.Replace(" ", "_")}.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al generar Excel", ex.Message, TipoMensaje.Error);
                return RedirectToAction("PanelControl", new { sid = sid });
            }
        }
        /// <summary>
        /// AJAX: Eliminar Zona en cascada y liberar asistentes.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEliminarZona(int idZona)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Gracias al ON DELETE CASCADE en PostgreSQL, borrar la zona elimina automáticamente 
                    // alojamientos, capacidades y asignaciones, liberando a la gente de nuevo a la Sala de Espera.
                    var cmd = new NpgsqlCommand(@"DELETE FROM ""Eventos_F_Zonas"" WHERE ""Id_Zona"" = @id", conexion);
                    cmd.Parameters.AddWithValue("@id", idZona);
                    await cmd.ExecuteNonQueryAsync();

                    return Json(new { exito = true });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Endpoint para crear una nueva Zona (Pestaña).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiCrearZona(int idEvento, string nombre)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_F_Zonas"" (""Id_Evento"", ""Nombre"") VALUES (@id, @nom) RETURNING ""Id_Zona""", conexion);
                    cmd.Parameters.AddWithValue("@id", idEvento);
                    cmd.Parameters.AddWithValue("@nom", nombre);
                    int idZona = (int)await cmd.ExecuteScalarAsync();
                    return Json(new { exito = true, idZona = idZona });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// Asignación masiva controlada.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiAsignarAlojamientoMultiple([FromBody] AsignacionMultipleRequest req)
        {
            try
            {
                if (req.IdsAsistentes == null || !req.IdsAsistentes.Any())
                    return Json(new { exito = false, mensaje = "No se recibieron personas para asignar." });

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. Obtener la capacidad y género actual del cuarto con bloqueo (FOR UPDATE)
                        // Se une a Eventos_F_Zonas para obtener el Id_Evento para SignalR
                        string sqlValCuarto = @"
            SELECT g.""Genero_Asignado"", c.""Capacidad_Total"", c.""Id_Subtipo"", c.""Id_Agrupacion"", z.""Id_Evento"",
                   (SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" a2 
                    JOIN ""Eventos_B_Asistentes"" b2 ON a2.""Id_Asistente"" = b2.""Id_Asistente"" 
                    WHERE a2.""Id_Alojamiento"" = g.""Id_Alojamiento"" 
                    AND (b2.""Id_Subtipo"" = c.""Id_Subtipo"" OR c.""Id_Agrupacion"" IN (SELECT ""Id_Agrupacion"" FROM ""Eventos_K_AgrupaModalidadesDetalle"" WHERE ""Id_Subtipo"" = b2.""Id_Subtipo""))) as ""Ocupados""
            FROM ""Eventos_G_Alojamientos"" g
            JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
            JOIN ""Eventos_H_Alojamiento_Capacidades"" c ON g.""Id_Alojamiento"" = c.""Id_Alojamiento""
            WHERE g.""Id_Alojamiento"" = @idAloj FOR UPDATE OF g";

                        string cuartoGenero = null;
                        int idEventoDetectado = 0;
                        var cuartoData = new Dictionary<string, (int Max, int Ocupados)>();

                        using (var cmdVal = new NpgsqlCommand(sqlValCuarto, conexion, trans))
                        {
                            cmdVal.Parameters.AddWithValue("@idAloj", req.IdAlojamiento);
                            using (var r = await cmdVal.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    if (idEventoDetectado == 0) idEventoDetectado = (int)r["Id_Evento"];
                                    cuartoGenero = r["Genero_Asignado"] != DBNull.Value ? r["Genero_Asignado"].ToString() : null;

                                    // Manejo seguro de nulos para evitar errores de casting
                                    int? idSub = r["Id_Subtipo"] != DBNull.Value ? (int?)r["Id_Subtipo"] : null;
                                    int? idAgr = r["Id_Agrupacion"] != DBNull.Value ? (int?)r["Id_Agrupacion"] : null;

                                    string key = idSub.HasValue ? $"SUB_{idSub}" : $"AGR_{idAgr}";
                                    cuartoData[key] = ((int)r["Capacidad_Total"], Convert.ToInt32(r["Ocupados"]));
                                }
                            }
                        }

                        if (!cuartoData.ContainsKey(req.RefDestino))
                            return Json(new { exito = false, mensaje = "El alojamiento no tiene configurada la modalidad o agrupación seleccionada." });

                        // 2. Traer información de los asistentes e identificar su agrupación
                        string sqlAsis = @"SELECT b.""Id_Asistente"", b.""Genero"", b.""Id_Subtipo"",
                          (SELECT d.""Id_Agrupacion"" FROM ""Eventos_K_AgrupaModalidadesDetalle"" d WHERE d.""Id_Subtipo"" = b.""Id_Subtipo"" LIMIT 1) as ""IdAgrupacionPertenece""
                          FROM ""Eventos_B_Asistentes"" b WHERE b.""Id_Asistente"" = ANY(@ids)";

                        var asisInfo = new List<(int Id, string Genero, int Subtipo, int? Agrupacion)>();

                        using (var cmdA = new NpgsqlCommand(sqlAsis, conexion, trans))
                        {
                            cmdA.Parameters.AddWithValue("@ids", req.IdsAsistentes);
                            using (var r = await cmdA.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                    asisInfo.Add((
                                        (int)r["Id_Asistente"],
                                        r["Genero"].ToString(),
                                        (int)r["Id_Subtipo"],
                                        r["IdAgrupacionPertenece"] != DBNull.Value ? (int?)r["IdAgrupacionPertenece"] : null
                                    ));
                            }
                        }

                        // --- BLOQUE DE PROTECCIÓN CONTRA DUPLICADOS (RESTAURADO) ---
                        var aIgnorar = new List<int>();
                        using (var cmdEx = new NpgsqlCommand(@"SELECT ""Id_Asistente"" FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Alojamiento"" = @idAloj AND ""Id_Asistente"" = ANY(@ids)", conexion, trans))
                        {
                            cmdEx.Parameters.AddWithValue("@idAloj", req.IdAlojamiento);
                            cmdEx.Parameters.AddWithValue("@ids", req.IdsAsistentes);
                            using (var rEx = await cmdEx.ExecuteReaderAsync())
                                while (await rEx.ReadAsync()) aIgnorar.Add((int)rEx[0]);
                        }
                        asisInfo = asisInfo.Where(x => !aIgnorar.Contains(x.Id)).ToList();

                        if (!asisInfo.Any()) return Json(new { exito = true, mensaje = "Ya estaban asignados." });
                        // ----------------------------------------------------------

                        // 3. Validaciones de Compatibilidad y Cupo
                        bool esDestinoAgrupacion = req.RefDestino.StartsWith("AGR_");
                        int idReferenciaDestino = int.Parse(req.RefDestino.Split('_')[1]);

                        foreach (var a in asisInfo)
                        {
                            if (esDestinoAgrupacion)
                            {
                                if (a.Agrupacion != idReferenciaDestino)
                                    return Json(new { exito = false, mensaje = $"El asistente {a.Id} no pertenece a la agrupación requerida." });
                            }
                            else
                            {
                                if (a.Subtipo != idReferenciaDestino)
                                    return Json(new { exito = false, mensaje = "Una o más personas no coinciden con la modalidad exacta de este espacio." });
                            }
                        }

                        if (cuartoData[req.RefDestino].Ocupados + asisInfo.Count > cuartoData[req.RefDestino].Max)
                            return Json(new { exito = false, mensaje = "No hay suficiente espacio disponible para el grupo seleccionado." });

                        // Corrección de género cruzado
                        string primerGeneroNuevo = asisInfo.First().Genero;
                        if (cuartoGenero != "X")
                        {
                            string generoRequerido = string.IsNullOrEmpty(cuartoGenero) ? primerGeneroNuevo : cuartoGenero;
                            if (asisInfo.Any(x => x.Genero != generoRequerido))
                                return Json(new { exito = false, mensaje = "Choque de géneros: No puedes mezclar géneros incompatibles en esta habitación." });
                        }

                        // 4. Inserción Atómica
                        string sqlIns = @"INSERT INTO ""Eventos_I_Alojamiento_Asignaciones"" (""Id_Alojamiento"", ""Id_Asistente"") VALUES (@a, @usr)";
                        foreach (var a in asisInfo)
                        {
                            var cmdDel = new NpgsqlCommand(@"DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Asistente"" = @id", conexion, trans);
                            cmdDel.Parameters.AddWithValue("@id", a.Id);
                            await cmdDel.ExecuteNonQueryAsync();

                            using (var cmdIns = new NpgsqlCommand(sqlIns, conexion, trans))
                            {
                                cmdIns.Parameters.AddWithValue("@a", req.IdAlojamiento);
                                cmdIns.Parameters.AddWithValue("@usr", a.Id);
                                await cmdIns.ExecuteNonQueryAsync();
                            }
                        }

                        // 5. Ajustar Género Neutral
                        if (string.IsNullOrEmpty(cuartoGenero))
                        {
                            var cmdGen = new NpgsqlCommand(@"UPDATE ""Eventos_G_Alojamientos"" SET ""Genero_Asignado"" = @g WHERE ""Id_Alojamiento"" = @a", conexion, trans);
                            cmdGen.Parameters.AddWithValue("@g", primerGeneroNuevo);
                            cmdGen.Parameters.AddWithValue("@a", req.IdAlojamiento);
                            await cmdGen.ExecuteNonQueryAsync();
                        }

                        await trans.CommitAsync();

                        // --- NOTIFICACIÓN SIGNALR EN VIVO ---
                        if (idEventoDetectado > 0)
                        {
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                        }

                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = "Error: " + ex.Message }); }
        }

        /// <summary>
        /// AJAX: Arrastrar y soltar (Asignar persona). Realiza validaciones estrictas.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiAsignarAlojamiento(int idAlojamiento, int idAsistente)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlValidacion = @"
                SELECT 
                    b.""Genero"" as AsistenteGenero, 
                    COALESCE(b.""Id_Subtipo"", 0) as AsistenteSubtipo,
                    g.""Genero_Asignado"" as AlojamientoGenero,
                    c.""Capacidad_Total"" as Capacidad,
                    (SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" a2 
                     JOIN ""Eventos_B_Asistentes"" b2 ON a2.""Id_Asistente"" = b2.""Id_Asistente"" 
                     WHERE a2.""Id_Alojamiento"" = @idAloj AND b2.""Id_Subtipo"" = b.""Id_Subtipo"") as Ocupados
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_G_Alojamientos"" g ON g.""Id_Alojamiento"" = @idAloj
                LEFT JOIN ""Eventos_H_Alojamiento_Capacidades"" c ON c.""Id_Alojamiento"" = @idAloj AND c.""Id_Subtipo"" = b.""Id_Subtipo""
                WHERE b.""Id_Asistente"" = @idAsis FOR UPDATE OF g";

                    string generoAsistente = "";
                    string generoAlojamiento = null;
                    int capacidad = 0;
                    int ocupados = 0;
                    bool modalidadAceptada = false;

                    using (var cmdVal = new NpgsqlCommand(sqlValidacion, conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@idAloj", idAlojamiento);
                        cmdVal.Parameters.AddWithValue("@idAsis", idAsistente);

                        using (var r = await cmdVal.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                generoAsistente = r["AsistenteGenero"].ToString();
                                generoAlojamiento = r["AlojamientoGenero"] != DBNull.Value ? r["AlojamientoGenero"].ToString() : null;

                                if (r["Capacidad"] != DBNull.Value)
                                {
                                    modalidadAceptada = true;
                                    capacidad = (int)r["Capacidad"];
                                    ocupados = Convert.ToInt32(r["Ocupados"]);
                                }
                            }
                            else return Json(new { exito = false, mensaje = "Asistente o alojamiento no encontrado." });
                        }
                    }

                    if (!string.IsNullOrEmpty(generoAlojamiento) && generoAlojamiento != "X" && generoAlojamiento != generoAsistente)
                        return Json(new { exito = false, mensaje = $"Este alojamiento es exclusivo para {(generoAlojamiento == "H" ? "Hombres" : "Mujeres")}." });

                    if (!modalidadAceptada)
                        return Json(new { exito = false, mensaje = "Este alojamiento no acepta esta modalidad." });

                    if (ocupados >= capacidad)
                        return Json(new { exito = false, mensaje = "No hay espacio disponible para esta modalidad." });

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        var cmdIns = new NpgsqlCommand(@"INSERT INTO ""Eventos_I_Alojamiento_Asignaciones"" (""Id_Alojamiento"", ""Id_Asistente"") VALUES (@a, @usr)", conexion, trans);
                        cmdIns.Parameters.AddWithValue("@a", idAlojamiento);
                        cmdIns.Parameters.AddWithValue("@usr", idAsistente);
                        await cmdIns.ExecuteNonQueryAsync();

                        if (string.IsNullOrEmpty(generoAlojamiento))
                        {
                            var cmdGen = new NpgsqlCommand(@"UPDATE ""Eventos_G_Alojamientos"" SET ""Genero_Asignado"" = @g WHERE ""Id_Alojamiento"" = @a", conexion, trans);
                            cmdGen.Parameters.AddWithValue("@g", generoAsistente);
                            cmdGen.Parameters.AddWithValue("@a", idAlojamiento);
                            await cmdGen.ExecuteNonQueryAsync();
                            generoAlojamiento = generoAsistente;
                        }
                        await trans.CommitAsync();
                    }

                    // --- SIGNALR NOTIFICATION ---
                    int idEventoDetectado = 0;
                    using (var cmdE = new NpgsqlCommand(@"SELECT z.""Id_Evento"" FROM ""Eventos_G_Alojamientos"" g JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona"" WHERE g.""Id_Alojamiento"" = @id", conexion))
                    {
                        cmdE.Parameters.AddWithValue("@id", idAlojamiento);
                        var resEv = await cmdE.ExecuteScalarAsync();
                        if (resEv != null) idEventoDetectado = (int)resEv;
                    }
                    if (idEventoDetectado > 0) await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");

                    return Json(new { exito = true, nuevoGenero = generoAlojamiento });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// Remover masivamente usuarios de sus cuartos.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiDesasignarAlojamientoMultiple([FromBody] DesasignacionMultipleRequest req)
        {
            try
            {
                if (req.IdsAsistentes == null || !req.IdsAsistentes.Any()) return Json(new { exito = true });

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        var idsAlojamientosAfectados = new HashSet<int>();
                        int idEventoDetectado = 0;

                        // 1. OBTENER LOS CUARTOS Y BLOQUEARLOS (CORREGIDO SIN 'DISTINCT')
                        // Dentro de ApiDesasignarAlojamientoMultiple
                        string sqlGetAloj = @"
                        SELECT g.""Id_Alojamiento"", z.""Id_Evento"" 
                        FROM ""Eventos_G_Alojamientos"" g
                        JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                        WHERE g.""Id_Alojamiento"" IN (
                            SELECT a.""Id_Alojamiento"" 
                            FROM ""Eventos_I_Alojamiento_Asignaciones"" a 
                            WHERE a.""Id_Asistente"" = ANY(@ids)
                        )
                        ORDER BY g.""Id_Alojamiento"" ASC 
                        FOR UPDATE OF g";

                        using (var cmdGet = new NpgsqlCommand(sqlGetAloj, conexion, trans))
                        {
                            cmdGet.Parameters.AddWithValue("@ids", req.IdsAsistentes);
                            using (var r = await cmdGet.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    idsAlojamientosAfectados.Add((int)r["Id_Alojamiento"]);
                                    if (idEventoDetectado == 0) idEventoDetectado = (int)r["Id_Evento"];
                                }
                            }
                        }

                        // 2. Eliminación masiva de las asignaciones
                        string sqlDel = @"DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Asistente"" = ANY(@ids)";
                        using (var cmdDel = new NpgsqlCommand(sqlDel, conexion, trans))
                        {
                            cmdDel.Parameters.AddWithValue("@ids", req.IdsAsistentes);
                            await cmdDel.ExecuteNonQueryAsync();
                        }

                        // 3. Revisar y liberar el género de los cuartos que quedaron vacíos
                        foreach (var idAloj in idsAlojamientosAfectados)
                        {
                            string sqlCheck = @"SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Alojamiento"" = @a";
                            long quedan = 0;
                            using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmdCheck.Parameters.AddWithValue("@a", idAloj);
                                quedan = (long)await cmdCheck.ExecuteScalarAsync();
                            }

                            if (quedan == 0)
                            {
                                string sqlFree = @"UPDATE ""Eventos_G_Alojamientos"" SET ""Genero_Asignado"" = NULL WHERE ""Id_Alojamiento"" = @a";
                                using (var cmdFree = new NpgsqlCommand(sqlFree, conexion, trans))
                                {
                                    cmdFree.Parameters.AddWithValue("@a", idAloj);
                                    await cmdFree.ExecuteNonQueryAsync();
                                }
                            }
                        }

                        await trans.CommitAsync();

                        // --- SIGNALR NOTIFICATION EN VIVO ---
                        if (idEventoDetectado > 0)
                        {
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                        }

                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiGuardarZona([FromBody] CrearZonaRequest req)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        int idZona = req.IdZona;

                        if (idZona == 0)
                        {
                            var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_F_Zonas"" (""Id_Evento"", ""Nombre"") VALUES (@id, @nom) RETURNING ""Id_Zona""", conexion, trans);
                            cmd.Parameters.AddWithValue("@id", req.IdEvento);
                            cmd.Parameters.AddWithValue("@nom", req.Nombre);
                            idZona = (int)await cmd.ExecuteScalarAsync();
                        }
                        else
                        {
                            var cmd = new NpgsqlCommand(@"UPDATE ""Eventos_F_Zonas"" SET ""Nombre"" = @nom WHERE ""Id_Zona"" = @id", conexion, trans);
                            cmd.Parameters.AddWithValue("@id", idZona);
                            cmd.Parameters.AddWithValue("@nom", req.Nombre);
                            await cmd.ExecuteNonQueryAsync();

                            // Limpiar modalidades anteriores
                            await new NpgsqlCommand($"DELETE FROM \"Eventos_F_Zonas_Subtipos\" WHERE \"Id_Zona\" = {idZona}", conexion, trans).ExecuteNonQueryAsync();
                        }

                        // Insertar las nuevas modalidades
                        if (req.SubtiposPermitidos != null)
                        {
                            foreach (int idSub in req.SubtiposPermitidos)
                            {
                                var cmdS = new NpgsqlCommand(@"INSERT INTO ""Eventos_F_Zonas_Subtipos"" (""Id_Zona"", ""Id_Subtipo"") VALUES (@z, @s)", conexion, trans);
                                cmdS.Parameters.AddWithValue("@z", idZona);
                                cmdS.Parameters.AddWithValue("@s", idSub);
                                await cmdS.ExecuteNonQueryAsync();
                            }
                        }

                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// AJAX: Quitar a una persona de su alojamiento.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiDesasignarAlojamiento(int idAsistente, int idAlojamiento)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        var cmdDel = new NpgsqlCommand(@"DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Asistente"" = @usr", conexion, trans);
                        cmdDel.Parameters.AddWithValue("@usr", idAsistente);
                        await cmdDel.ExecuteNonQueryAsync();

                        var cmdCheck = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Alojamiento"" = @a", conexion, trans);
                        cmdCheck.Parameters.AddWithValue("@a", idAlojamiento);
                        long quedan = (long)await cmdCheck.ExecuteScalarAsync();

                        if (quedan == 0)
                        {
                            var cmdFree = new NpgsqlCommand(@"UPDATE ""Eventos_G_Alojamientos"" SET ""Genero_Asignado"" = NULL WHERE ""Id_Alojamiento"" = @a", conexion, trans);
                            cmdFree.Parameters.AddWithValue("@a", idAlojamiento);
                            await cmdFree.ExecuteNonQueryAsync();
                        }

                        await trans.CommitAsync();

                        // --- SIGNALR NOTIFICATION ---
                        int idEventoDetectado = 0;
                        using (var cmdE = new NpgsqlCommand(@"SELECT z.""Id_Evento"" FROM ""Eventos_G_Alojamientos"" g JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona"" WHERE g.""Id_Alojamiento"" = @id", conexion))
                        {
                            cmdE.Parameters.AddWithValue("@id", idAlojamiento);
                            var resEv = await cmdE.ExecuteScalarAsync();
                            if (resEv != null) idEventoDetectado = (int)resEv;
                        }
                        if (idEventoDetectado > 0) await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");

                        return Json(new { exito = true, quedoVacia = (quedan == 0) });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }
        /// <summary>
        /// AJAX: Edita el nombre de una Zona existente
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEditarZona(int idZona, string nombre)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var cmd = new NpgsqlCommand(@"UPDATE ""Eventos_F_Zonas"" SET ""Nombre"" = @nom WHERE ""Id_Zona"" = @id", conexion);
                    cmd.Parameters.AddWithValue("@id", idZona);
                    cmd.Parameters.AddWithValue("@nom", nombre);
                    await cmd.ExecuteNonQueryAsync();
                    return Json(new { exito = true });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }


        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiCrearAlojamiento([FromBody] EditarAlojamientoHibridoRequest req)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_G_Alojamientos"" (""Id_Zona"", ""Nombre"", ""Genero_Asignado"") VALUES (@z, @nom, @gen) RETURNING ""Id_Alojamiento""", conexion, trans);
                        cmd.Parameters.AddWithValue("@z", req.IdZona);
                        cmd.Parameters.AddWithValue("@nom", req.Nombre);
                        cmd.Parameters.AddWithValue("@gen", string.IsNullOrEmpty(req.GeneroAsignado) ? DBNull.Value : (object)req.GeneroAsignado);

                        int idAlojamiento = (int)await cmd.ExecuteScalarAsync();

                        foreach (var cap in req.CapacidadesArray)
                        {
                            var cmdCap = new NpgsqlCommand(@"INSERT INTO ""Eventos_H_Alojamiento_Capacidades"" (""Id_Alojamiento"", ""Id_Subtipo"", ""Id_Agrupacion"", ""Capacidad_Total"") VALUES (@a, @s, @ag, @tot)", conexion, trans);
                            cmdCap.Parameters.AddWithValue("@a", idAlojamiento);
                            cmdCap.Parameters.AddWithValue("@s", cap.IdSubtipo.HasValue ? (object)cap.IdSubtipo.Value : DBNull.Value);
                            cmdCap.Parameters.AddWithValue("@ag", cap.IdAgrupacion.HasValue ? (object)cap.IdAgrupacion.Value : DBNull.Value);
                            cmdCap.Parameters.AddWithValue("@tot", cap.CapacidadTotal);
                            await cmdCap.ExecuteNonQueryAsync();
                        }
                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiCrearAlojamientoMultiple([FromBody] List<EditarAlojamientoHibridoRequest> reqs)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        foreach (var req in reqs)
                        {
                            var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_G_Alojamientos"" (""Id_Zona"", ""Nombre"", ""Genero_Asignado"") VALUES (@z, @nom, @gen) RETURNING ""Id_Alojamiento""", conexion, trans);
                            cmd.Parameters.AddWithValue("@z", req.IdZona);
                            cmd.Parameters.AddWithValue("@nom", req.Nombre);
                            cmd.Parameters.AddWithValue("@gen", string.IsNullOrEmpty(req.GeneroAsignado) ? DBNull.Value : (object)req.GeneroAsignado);

                            int idAlojamiento = (int)await cmd.ExecuteScalarAsync();

                            foreach (var cap in req.CapacidadesArray)
                            {
                                var cmdCap = new NpgsqlCommand(@"INSERT INTO ""Eventos_H_Alojamiento_Capacidades"" (""Id_Alojamiento"", ""Id_Subtipo"", ""Id_Agrupacion"", ""Capacidad_Total"") VALUES (@a, @s, @ag, @tot)", conexion, trans);
                                cmdCap.Parameters.AddWithValue("@a", idAlojamiento);
                                cmdCap.Parameters.AddWithValue("@s", cap.IdSubtipo.HasValue ? (object)cap.IdSubtipo.Value : DBNull.Value);
                                cmdCap.Parameters.AddWithValue("@ag", cap.IdAgrupacion.HasValue ? (object)cap.IdAgrupacion.Value : DBNull.Value);
                                cmdCap.Parameters.AddWithValue("@tot", cap.CapacidadTotal);
                                await cmdCap.ExecuteNonQueryAsync();
                            }
                        }

                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEditarAlojamiento([FromBody] EditarAlojamientoHibridoRequest req)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Actualizar Nombre y Género
                        var cmdA = new NpgsqlCommand(@"UPDATE ""Eventos_G_Alojamientos"" SET ""Nombre"" = @nom, ""Genero_Asignado"" = @gen WHERE ""Id_Alojamiento"" = @id", conexion, trans);
                        cmdA.Parameters.AddWithValue("@id", req.IdAlojamiento);
                        cmdA.Parameters.AddWithValue("@nom", req.Nombre);
                        cmdA.Parameters.AddWithValue("@gen", string.IsNullOrEmpty(req.GeneroAsignado) ? DBNull.Value : (object)req.GeneroAsignado);
                        await cmdA.ExecuteNonQueryAsync();

                        // Borrón y cuenta nueva de capacidades (Simplificado)
                        // 1. Borramos todas las capacidades previas que NO tengan personas asignadas
                        await new NpgsqlCommand($"DELETE FROM \"Eventos_H_Alojamiento_Capacidades\" WHERE \"Id_Alojamiento\" = {req.IdAlojamiento} AND NOT EXISTS (SELECT 1 FROM \"Eventos_I_Alojamiento_Asignaciones\" a JOIN \"Eventos_B_Asistentes\" b ON a.\"Id_Asistente\" = b.\"Id_Asistente\" WHERE a.\"Id_Alojamiento\" = \"Eventos_H_Alojamiento_Capacidades\".\"Id_Alojamiento\" AND (b.\"Id_Subtipo\" = \"Eventos_H_Alojamiento_Capacidades\".\"Id_Subtipo\" OR \"Eventos_H_Alojamiento_Capacidades\".\"Id_Agrupacion\" IN (SELECT \"Id_Agrupacion\" FROM \"Eventos_K_AgrupaModalidadesDetalle\" WHERE \"Id_Subtipo\" = b.\"Id_Subtipo\")))", conexion, trans).ExecuteNonQueryAsync();

                        // 2. Insertamos o Actualizamos las capacidades enviadas desde la vista
                        foreach (var cap in req.CapacidadesArray)
                        {
                            string sqlCheck = @"SELECT ""Id_Capacidad"" FROM ""Eventos_H_Alojamiento_Capacidades"" WHERE ""Id_Alojamiento"" = @a AND (""Id_Subtipo"" = @s OR (""Id_Subtipo"" IS NULL AND @s IS NULL)) AND (""Id_Agrupacion"" = @ag OR (""Id_Agrupacion"" IS NULL AND @ag IS NULL))";
                            int? idCap = null;
                            using (var cmdC = new NpgsqlCommand(sqlCheck, conexion, trans))
                            {
                                cmdC.Parameters.AddWithValue("@a", req.IdAlojamiento);
                                cmdC.Parameters.AddWithValue("@s", cap.IdSubtipo ?? (object)DBNull.Value);
                                cmdC.Parameters.AddWithValue("@ag", cap.IdAgrupacion ?? (object)DBNull.Value);
                                var r = await cmdC.ExecuteScalarAsync();
                                if (r != null) idCap = (int)r;
                            }

                            if (idCap.HasValue)
                            {
                                var cmdUpd = new NpgsqlCommand(@"UPDATE ""Eventos_H_Alojamiento_Capacidades"" SET ""Capacidad_Total"" = @tot WHERE ""Id_Capacidad"" = @id", conexion, trans);
                                cmdUpd.Parameters.AddWithValue("@tot", cap.CapacidadTotal);
                                cmdUpd.Parameters.AddWithValue("@id", idCap.Value);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }
                            else
                            {
                                var cmdIns = new NpgsqlCommand(@"INSERT INTO ""Eventos_H_Alojamiento_Capacidades"" (""Id_Alojamiento"", ""Id_Subtipo"", ""Id_Agrupacion"", ""Capacidad_Total"") VALUES (@a, @s, @ag, @tot)", conexion, trans);
                                cmdIns.Parameters.AddWithValue("@a", req.IdAlojamiento);
                                cmdIns.Parameters.AddWithValue("@s", cap.IdSubtipo ?? (object)DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@ag", cap.IdAgrupacion ?? (object)DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@tot", cap.CapacidadTotal);
                                await cmdIns.ExecuteNonQueryAsync();
                            }
                        }

                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// Descarga en Excel a todas las personas que ya pagaron pero aún no tienen cuarto asignado.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DescargarPendientesAlojamientoExcel(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Error", "No tienes permisos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);
            string tituloEvento = "";

            try
            {
                using (var wb = new ClosedXML.Excel.XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("Lista de Espera");

                    // Cabeceras
                    ws.Cell(1, 1).Value = "ID Asistente";
                    ws.Cell(1, 2).Value = "Nombre del Asistente";
                    ws.Cell(1, 3).Value = "Edad";
                    ws.Cell(1, 4).Value = "Género";
                    ws.Cell(1, 5).Value = "Modalidad";
                    ws.Cell(1, 6).Value = "Score (Prioridad)";
                    ws.Cell(1, 7).Value = "Inscrito Por (Nombre)";
                    ws.Cell(1, 8).Value = "Usuario (Nick de quien inscribió)";
                    ws.Cell(1, 9).Value = "Teléfono de quien inscribió";
                    ws.Cell(1, 10).Value = "Correo de quien inscribió";

                    var rngHead = ws.Range("A1:J1");
                    rngHead.Style.Font.SetBold().Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                    rngHead.Style.Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#c0392b")); // Rojo para destacar que están "pendientes"

                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();

                        // Título del evento para nombrar el archivo
                        var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion);
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        tituloEvento = (string)await cmdEv.ExecuteScalarAsync();

                        // Consulta de los NO ASIGNADOS + Datos del Registrador
                        string sql = @"
                            SELECT 
                                b.""Id_Asistente"", b.""Nombre_Completo"", b.""Edad"", b.""Genero"",
                                COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                                COALESCE(j.""Puntaje"", 0) as ""Score"",
                                u.""NombreCompleto"" as ""LiderNombre"",
                                u.""Nombre_Usuario"" as ""LiderNick"",
                                u.""Telefono"" as ""LiderTel"",
                                u.""Email"" as ""LiderEmail""
                            FROM ""Eventos_B_Asistentes"" b
                            JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                            JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                            LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                            LEFT JOIN ""Eventos_J_Prioridad_Alojamiento"" j ON b.""Id_Asistente"" = j.""Id_Asistente""
                            WHERE r.""Id_Evento"" = @id 
                            AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE)
                            AND NOT EXISTS (SELECT 1 FROM ""Eventos_I_Alojamiento_Asignaciones"" a WHERE a.""Id_Asistente"" = b.""Id_Asistente"")
                            ORDER BY j.""Puntaje"" DESC, b.""Nombre_Completo"" ASC";

                        int f = 2;
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idEvento);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    ws.Cell(f, 1).Value = (int)r["Id_Asistente"];
                                    ws.Cell(f, 2).Value = r["Nombre_Completo"].ToString();
                                    ws.Cell(f, 3).Value = (int)r["Edad"];
                                    ws.Cell(f, 4).Value = r["Genero"].ToString() == "H" ? "Hombre" : "Mujer";
                                    ws.Cell(f, 5).Value = r["Modalidad"].ToString();

                                    ws.Cell(f, 6).Value = (decimal)r["Score"];
                                    ws.Cell(f, 6).Style.NumberFormat.Format = "0.0"; // Formato un decimal

                                    ws.Cell(f, 7).Value = r["LiderNombre"].ToString();
                                    ws.Cell(f, 8).Value = r["LiderNick"].ToString();
                                    ws.Cell(f, 9).Value = r["LiderTel"]?.ToString() ?? "N/D";
                                    ws.Cell(f, 10).Value = r["LiderEmail"].ToString();
                                    f++;
                                }
                            }
                        }
                    }

                    ws.Columns().AdjustToContents();

                    using (var stream = new MemoryStream())
                    {
                        wb.SaveAs(stream);
                        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"ListaEspera_{tituloEvento.Replace(" ", "_")}.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo generar el reporte: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("DistribucionAlojamientos", new { sid = sid });
            }
        }

        [Authorize]
        public async Task<IActionResult> EditorImagenPortada(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar)) return Forbid();

            int idEvento = Funciones.DesencriptarId(sid);
            var modelo = new EditorGraficoViewModel { IdEvento = idEvento, IdEventoEncriptado = sid };

            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();

                // 1. Obtener datos básicos
                using (var cmd = new NpgsqlCommand(@"SELECT ""Titulo"", ""Imagen_Url"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con))
                {
                    cmd.Parameters.AddWithValue("@id", idEvento);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            modelo.TituloEvento = r["Titulo"].ToString();
                            modelo.ImagenUrlActual = r["Imagen_Url"].ToString();
                        }
                        else return RedirectToAction("Index");
                    }
                }

                // 2. Obtener modalidades y sus coordenadas ORDENADAS ALFABÉTICAMENTE
                string sql = @"
            SELECT s.""Id_Subtipo"", s.""Nombre"", g.""Pos_X"", g.""Pos_Y""
            FROM ""Eventos_Subtipos"" s
            LEFT JOIN ""Eventos_Modalidades_Grafico"" g ON s.""Id_Subtipo"" = g.""Id_Subtipo""
            WHERE s.""Id_Evento"" = @id AND s.""Activo"" = TRUE
            ORDER BY s.""Nombre"" ASC"; 

                using (var cmd = new NpgsqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@id", idEvento);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            modelo.Modalidades.Add(new ItemModalidadGrafico
                            {
                                IdSubtipo = (int)r["Id_Subtipo"],
                                Nombre = r["Nombre"].ToString(),
                                X = r["Pos_X"] != DBNull.Value ? (decimal)r["Pos_X"] : -1,
                                Y = r["Pos_Y"] != DBNull.Value ? (decimal)r["Pos_Y"] : -1,
                                Configurado = r["Pos_X"] != DBNull.Value
                            });
                        }
                    }
                }
            }
            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarConfiguracionGrafica(int idEvento, string jsonCoordenadas)
        {
            var coords = System.Text.Json.JsonSerializer.Deserialize<List<ItemModalidadGrafico>>(jsonCoordenadas);

            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();
                using (var trans = await con.BeginTransactionAsync())
                {
                    try
                    {
                        // Limpiar configuración previa
                        await new NpgsqlCommand($@"DELETE FROM ""Eventos_Modalidades_Grafico"" WHERE ""Id_Evento"" = {idEvento}", con, trans).ExecuteNonQueryAsync();

                        // Insertar nuevas posiciones
                        string sqlIns = @"INSERT INTO ""Eventos_Modalidades_Grafico"" (""Id_Evento"", ""Id_Subtipo"", ""Pos_X"", ""Pos_Y"", ""Ultimos_Ocupados_Generados"") VALUES (@ev, @sub, @x, @y, -1)";

                        foreach (var c in coords.Where(x => x.Configurado))
                        {
                            using (var cmd = new NpgsqlCommand(sqlIns, con, trans))
                            {
                                cmd.Parameters.AddWithValue("@ev", idEvento);
                                cmd.Parameters.AddWithValue("@sub", c.IdSubtipo);
                                cmd.Parameters.AddWithValue("@x", c.X);
                                cmd.Parameters.AddWithValue("@y", c.Y);
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }

                        await trans.CommitAsync();
                        MostrarMensaje("Mapa Guardado", "Las posiciones de los sellos se han registrado correctamente.", TipoMensaje.Exito);
                    }
                    catch (Exception ex)
                    {
                        await trans.RollbackAsync();
                        MostrarMensaje("Error", "No se pudo guardar el mapa: " + ex.Message, TipoMensaje.Error);
                    }
                }
            }

            // Ejecutamos esto inmediatamente después de guardar el mapa
            await ProcesarImagenConSellos(idEvento);

            // Aquí llamaríamos a la función de reprocesamiento de imagen en el siguiente paso
            return RedirectToAction("Editor", new { sid = Funciones.EncriptarId(idEvento) });
        }

        /// <summary>
        /// Función que evalúa si existen modalidades agotadas.
        /// Si las hay, dibuja "tacha.png" sobre la imagen original y actualiza la ImagenDinamica.
        /// </summary>
        private async Task ProcesarImagenConSellos(int idEvento)
        {
            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();

                // 1. OBTENER URL ORIGINAL Y DATOS DINÁMICOS ACTUALES
                string urlOriginal = "";
                string urlDinamicaVieja = "";
                bool dinamicaActiva = false;

                using (var cmd = new NpgsqlCommand(@"SELECT ""Imagen_Url"", ""ImagenDinamica_Url"", ""ImagenDinamica"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con))
                {
                    cmd.Parameters.AddWithValue("@id", idEvento);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            urlOriginal = r["Imagen_Url"]?.ToString();
                            urlDinamicaVieja = r["ImagenDinamica_Url"]?.ToString();
                            dinamicaActiva = r["ImagenDinamica"] != DBNull.Value && (bool)r["ImagenDinamica"];
                        }
                    }
                }

                if (string.IsNullOrEmpty(urlOriginal)) return;

                // 2. OBTENER COORDENADAS Y ESTADO PREVIO DE AGOTADOS
                var coordenadas = new List<ItemModalidadGrafico>();
                string sqlConf = @"SELECT ""Id_Subtipo"", ""Pos_X"", ""Pos_Y"", ""Ultimos_Ocupados_Generados"" FROM ""Eventos_Modalidades_Grafico"" WHERE ""Id_Evento"" = @id";

                using (var cmd = new NpgsqlCommand(sqlConf, con))
                {
                    cmd.Parameters.AddWithValue("@id", idEvento);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            coordenadas.Add(new ItemModalidadGrafico
                            {
                                IdSubtipo = (int)r["Id_Subtipo"],
                                X = (decimal)r["Pos_X"],
                                Y = (decimal)r["Pos_Y"],
                                // Usamos Configurado para mapear temporalmente si estaba agotada en la última generación
                                Configurado = r["Ultimos_Ocupados_Generados"] != DBNull.Value && (int)r["Ultimos_Ocupados_Generados"] == 1
                            });
                        }
                    }
                }

                if (!coordenadas.Any())
                {
                    await new NpgsqlCommand($"UPDATE \"Eventos_Catalogo\" SET \"ImagenDinamica\" = FALSE WHERE \"Id_Evento\" = {idEvento}", con).ExecuteNonQueryAsync();
                    return;
                }

                // 3. CONSULTAR DISPONIBILIDAD REAL ACTUAL
                var disp = await ConsultarDisponibilidadEvento(idEvento, con);
                if (disp == null) return;

                var modalidadesAgotadasActuales = new List<ItemModalidadGrafico>();
                bool requiereGenerarImagen = false;

                foreach (var c in coordenadas)
                {
                    var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == c.IdSubtipo);
                    bool estaAgotadaAhora = modal != null && modal.LugaresOcupados >= modal.CupoTotal;
                    bool estabaAgotadaAntes = c.Configurado;

                    // Si el estado de agotado cambió para cualquier modalidad, marcamos que se requiere nueva imagen
                    if (estaAgotadaAhora != estabaAgotadaAntes) requiereGenerarImagen = true;

                    if (estaAgotadaAhora) modalidadesAgotadasActuales.Add(c);
                }

                // --- ESCUDO DE RENDIMIENTO ---
                // Si no hay cambios en quién está agotado y ya existe una imagen dinámica, no hacemos nada
                if (!requiereGenerarImagen && dinamicaActiva) return;

                // 4. SI NADIE ESTÁ AGOTADO: Desactivamos dinámica y limpiamos rastros
                if (!modalidadesAgotadasActuales.Any())
                {
                    await new NpgsqlCommand($"UPDATE \"Eventos_Catalogo\" SET \"ImagenDinamica\" = FALSE WHERE \"Id_Evento\" = {idEvento}", con).ExecuteNonQueryAsync();
                    await new NpgsqlCommand($"UPDATE \"Eventos_Modalidades_Grafico\" SET \"Ultimos_Ocupados_Generados\" = 0 WHERE \"Id_Evento\" = {idEvento}", con).ExecuteNonQueryAsync();
                    return;
                }

                // 5. HAY CAMBIOS: PROCESAR IMAGEN CON IMAGESHARP
                try
                {
                    using (var httpClient = new HttpClient())
                    {
                        byte[] imageBytes = await httpClient.GetByteArrayAsync(urlOriginal);

                        using (var imageBase = SixLabors.ImageSharp.Image.Load(imageBytes))
                        {
                            var env = HttpContext.RequestServices.GetService<IWebHostEnvironment>();
                            string tachaPath = Path.Combine(env.WebRootPath, "Images", "tacha.png");

                            if (System.IO.File.Exists(tachaPath))
                            {
                                using (var tachaImg = SixLabors.ImageSharp.Image.Load(tachaPath))
                                {
                                    // Tamaño proporcional (3% del ancho de la base)
                                    int markerSize = (int)(imageBase.Width * 0.03m);
                                    if (markerSize < 15) markerSize = 15;
                                    if (markerSize > 150) markerSize = 150;

                                    tachaImg.Mutate(x => x.Resize(markerSize, markerSize));

                                    var grafOptions = new SixLabors.ImageSharp.GraphicsOptions
                                    {
                                        Antialias = true,
                                        AlphaCompositionMode = SixLabors.ImageSharp.PixelFormats.PixelAlphaCompositionMode.SrcOver
                                    };

                                    foreach (var agotado in modalidadesAgotadasActuales)
                                    {
                                        int posX = (int)((imageBase.Width * agotado.X) / 100m) - (markerSize / 2);
                                        int posY = (int)((imageBase.Height * agotado.Y) / 100m) - (markerSize / 2);
                                        imageBase.Mutate(ctx => ctx.DrawImage(tachaImg, new SixLabors.ImageSharp.Point(posX, posY), grafOptions));
                                    }
                                }
                            }

                            // 6. GUARDAR COMO PNG Y SUBIR
                            using (var ms = new MemoryStream())
                            {
                                await imageBase.SaveAsPngAsync(ms); // Preserva transparencia
                                ms.Position = 0;

                                var formFile = new FormFile(ms, 0, ms.Length, "dinamica", $"dinamica_{idEvento}.png")
                                {
                                    Headers = new HeaderDictionary(),
                                    ContentType = "image/png"
                                };

                                string urlNuevaDinamica = await SubirImagenCloudinary(formFile, $"{sAmbiente}/Imágenes/Eventos/{idEvento}/Dinamicas");

                                // 7. ACTUALIZAR BASE DE DATOS TRANSACCIONALMENTE
                                using (var trans = await con.BeginTransactionAsync())
                                {
                                    // Actualizar URL en catálogo
                                    string sqlUpdate = @"UPDATE ""Eventos_Catalogo"" SET ""ImagenDinamica_Url"" = @url, ""ImagenDinamica"" = TRUE WHERE ""Id_Evento"" = @id";
                                    using (var cmdUpd = new NpgsqlCommand(sqlUpdate, con, trans))
                                    {
                                        cmdUpd.Parameters.AddWithValue("@url", urlNuevaDinamica);
                                        cmdUpd.Parameters.AddWithValue("@id", idEvento);
                                        await cmdUpd.ExecuteNonQueryAsync();
                                    }

                                    // Actualizar estados de agotado en el mapa gráfico
                                    await new NpgsqlCommand($"UPDATE \"Eventos_Modalidades_Grafico\" SET \"Ultimos_Ocupados_Generados\" = 0 WHERE \"Id_Evento\" = {idEvento}", con, trans).ExecuteNonQueryAsync();
                                    foreach (var agotado in modalidadesAgotadasActuales)
                                    {
                                        await new NpgsqlCommand($"UPDATE \"Eventos_Modalidades_Grafico\" SET \"Ultimos_Ocupados_Generados\" = 1 WHERE \"Id_Evento\" = {idEvento} AND \"Id_Subtipo\" = {agotado.IdSubtipo}", con, trans).ExecuteNonQueryAsync();
                                    }

                                    await trans.CommitAsync();
                                }

                                // 8. LIMPIEZA DE CLOUDINARY
                                if (!string.IsNullOrEmpty(urlDinamicaVieja) && urlDinamicaVieja != urlNuevaDinamica && urlDinamicaVieja.Contains("cloudinary.com"))
                                {
                                    try
                                    {
                                        var uriOld = new Uri(urlDinamicaVieja);
                                        var segmentsOld = uriOld.Segments;
                                        int uploadIndexOld = Array.IndexOf(segmentsOld, "upload/");
                                        if (uploadIndexOld >= 0 && segmentsOld.Length > uploadIndexOld + 2)
                                        {
                                            string publicIdOldExt = string.Join("", segmentsOld.Skip(uploadIndexOld + 2));
                                            string publicIdOld = Path.ChangeExtension(publicIdOldExt, null).Replace("%20", " ").Trim('/');
                                            await _cloudinary.DestroyAsync(new DeletionParams(publicIdOld));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error al procesar imagen dinámica: " + ex.Message);
                }
            }
        }


        [Authorize]
        public async Task<IActionResult> RegistroRapido(string sid)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            int idEvento = Funciones.DesencriptarId(sid);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. LIMPIEZA INICIAL: Borrar (PENDIENTE) que ya expiraron (>30 min)
                    await LimpiarPendientesExpirados(idEvento, idUser, conexion);

                    // 2. VALIDACIÓN DE ACCESO
                    if (!await ValidarAccesoAEvento(idEvento, idUser, conexion)) return RedirectToAction("Index");

                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "El pre-registro rápido se encuentra deshabilitado.", TipoMensaje.Alerta);
                        return RedirectToAction("Registro", new { sid = sid });
                    }

                    var disp = await ConsultarDisponibilidadEvento(idEvento, conexion);
                    if (disp == null || !disp.EventoActivo) return RedirectToAction("Index");

                    // 3. CARGAR MAPA Y MIS APARTADOS ACTUALES
                    var mapaData = new List<dynamic>();
                    string sqlM = @"SELECT s.""Id_Subtipo"", s.""Nombre"", g.""Pos_X"", g.""Pos_Y""
                            FROM ""Eventos_Subtipos"" s
                            JOIN ""Eventos_Modalidades_Grafico"" g ON s.""Id_Subtipo"" = g.""Id_Subtipo""
                            WHERE s.""Id_Evento"" = @id AND s.""Activo"" = TRUE";

                    using (var cmd = new NpgsqlCommand(sqlM, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idSub = (int)r["Id_Subtipo"];
                                var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == idSub);

                                mapaData.Add(new
                                {
                                    id = idSub,
                                    nombre = r["Nombre"].ToString(),
                                    x = r["Pos_X"],
                                    y = r["Pos_Y"],
                                    disp = modal?.LugaresDisponibles ?? 0,
                                    costo = modal?.Costo ?? 0
                                });
                            }
                        }
                    }

                    ViewBag.IdEvento = idEvento;
                    ViewBag.IdEventoEncriptado = sid;
                    ViewBag.Titulo = disp.Titulo;
                    ViewBag.ImagenUrl = (await new NpgsqlCommand($"SELECT \"Imagen_Url\" FROM \"Eventos_Catalogo\" WHERE \"Id_Evento\"={idEvento}", conexion).ExecuteScalarAsync())?.ToString();
                    ViewBag.MapaJson = System.Text.Json.JsonSerializer.Serialize(mapaData);

                    return View();
                }
            }
            catch (Exception ex) { return RedirectToAction("Index"); }
        }

        /// <summary>
        /// AJAX: Obtiene la disponibilidad pero separa los lugares que son MÍOS (PENDIENTE).
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetDisponibilidadMapa(int idEvento)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    // 1. Disponibilidad Global (la que ya tenemos)
                    var disp = await ConsultarDisponibilidadEvento(idEvento, con, forUpdate: false);

                    // 2. Consultar específicamente cuántos (PENDIENTE) tiene este usuario por cada modalidad
                    var misPendientes = new Dictionary<int, int>();
                    string sqlMios = @"
                SELECT b.""Id_Subtipo"", COUNT(*) 
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                WHERE r.""Id_Evento"" = @ev AND r.""Id_Usuario"" = @uid 
                AND b.""Nombre_Completo"" = '(PENDIENTE)'
                AND r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes'
                GROUP BY b.""Id_Subtipo""";

                    using (var cmd = new NpgsqlCommand(sqlMios, con))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                            while (await r.ReadAsync()) misPendientes[Convert.ToInt32(r[0])] = Convert.ToInt32(r[1]);
                    }

                    var resumen = disp.Modalidades.Select(m => new {
                        id = m.IdSubtipo,
                        disp = m.LugaresDisponibles, // Disponibilidad para el resto del mundo
                        mios = misPendientes.ContainsKey(m.IdSubtipo) ? misPendientes[m.IdSubtipo] : 0
                    });

                    return Json(resumen);
                }
            }
            catch { return BadRequest(); }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApiReservarLugarRapido([FromBody] ReservaItem req)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Limpieza simple: Como no hay deuda, un DELETE basta
                    string sqlClean = @"DELETE FROM ""Eventos_B_Asistentes"" 
                                WHERE ""Id_Subtipo"" = @sub AND ""Nombre_Completo"" = '(PENDIENTE)'
                                AND ""Id_Registro"" IN (SELECT ""Id_Registro"" FROM ""Eventos_A_Registros"" WHERE ""Id_Usuario"" = @uid AND ""Id_Evento"" = @ev)";
                    using (var cmdDel = new NpgsqlCommand(sqlClean, conexion))
                    {
                        cmdDel.Parameters.AddWithValue("@sub", req.IdSubtipo);
                        cmdDel.Parameters.AddWithValue("@uid", idUser);
                        cmdDel.Parameters.AddWithValue("@ev", req.IdEvento);
                        await cmdDel.ExecuteNonQueryAsync();
                    }

                    if (await ValidarEventoBloqueado(req.IdEvento, conexion))
                        return Json(new { exito = false, mensaje = "El evento se encuentra bloqueado por logística." });

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        var disp = await ConsultarDisponibilidadEvento(req.IdEvento, conexion, trans, forUpdate: true);
                        var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == req.IdSubtipo);

                        if (modal == null)
                        {
                            return Json(new { exito = false, mensaje = "La opción seleccionada no existe." });
                        }

                        if (modal.LugaresDisponibles < req.Cantidad)
                        {
                            // Si ya no hay nada
                            if (modal.LugaresDisponibles == 0)
                            {
                                return Json(new { exito = false, mensaje = "Este lugar ya se encuentra totalmente agotado." });
                            }

                            // Calculamos cuántos le faltan
                            int faltantes = req.Cantidad - modal.LugaresDisponibles;

                            return Json(new
                            {
                                exito = false,
                                mensaje = $"Solo quedan {modal.LugaresDisponibles} lugares disponibles. Te faltan {faltantes} cupos para los {req.Cantidad} que solicitas."
                            });
                        }

                        int idReg = await ObtenerOCrearRegistroPadre(req.IdEvento, idUser, conexion, trans);

                        for (int i = 0; i < req.Cantidad; i++)
                        {
                            string sqlA = @"INSERT INTO ""Eventos_B_Asistentes"" (""Id_Registro"", ""Nombre_Completo"", ""Token_Pago_Externo"", ""Edad"", ""Genero"", ""Id_Subtipo"", ""Es_Pagado"") 
                                    VALUES (@reg, '(PENDIENTE)', @tok, 18, 'H', @sub, FALSE)";
                            using (var cmdA = new NpgsqlCommand(sqlA, conexion, trans))
                            {
                                cmdA.Parameters.AddWithValue("@reg", idReg);
                                cmdA.Parameters.AddWithValue("@tok", GenerarTokenAmigable("(PENDIENTE)"));
                                cmdA.Parameters.AddWithValue("@sub", req.IdSubtipo);
                                await cmdA.ExecuteNonQueryAsync();
                            }
                        }
                        await trans.CommitAsync();

                        // --- AVISO EN TIEMPO REAL A TODOS LOS NAVEGADORES DEL MISMO EVENTO ---
                        await _eventosHub.Clients.Group($"Evento_{req.IdEvento}").SendAsync("RefrescarMapa");

                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApiEliminarLugarRapido(int idAsistente)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // Obtenemos el Id del evento antes de borrarlo para poder notificar al grupo
                    int idEvento = 0;
                    string sqlEv = @"SELECT r.""Id_Evento"" 
                                     FROM ""Eventos_B_Asistentes"" b 
                                     JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                     WHERE b.""Id_Asistente"" = @idA";
                    using (var cmdEv = new NpgsqlCommand(sqlEv, con))
                    {
                        cmdEv.Parameters.AddWithValue("@idA", idAsistente);
                        var res = await cmdEv.ExecuteScalarAsync();
                        if (res != null) idEvento = (int)res;
                    }

                    // Seguridad: Solo borrar si es (PENDIENTE) y pertenece al usuario
                    string sqlDel = @"DELETE FROM ""Eventos_B_Asistentes"" 
                              WHERE ""Id_Asistente"" = @idA AND ""Nombre_Completo"" = '(PENDIENTE)'
                              AND ""Id_Registro"" IN (SELECT ""Id_Registro"" FROM ""Eventos_A_Registros"" WHERE ""Id_Usuario"" = @uid)";

                    using (var cmd = new NpgsqlCommand(sqlDel, con))
                    {
                        cmd.Parameters.AddWithValue("@idA", idAsistente);
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // --- AVISO EN TIEMPO REAL SI SE LOGRÓ OBTENER EL EVENTO ---
                    if (idEvento > 0)
                    {
                        await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("RefrescarMapa");
                    }

                    return Json(new { exito = true });
                }
            }
            catch { return Json(new { exito = false }); }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LimpiarModalidadPendiente(int idEvento, int idSubtipo)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    // Borrado directo (sin riesgo de FK de deudas)
                    string sql = @"DELETE FROM ""Eventos_B_Asistentes"" 
                           WHERE ""Id_Subtipo"" = @sub AND ""Nombre_Completo"" = '(PENDIENTE)'
                           AND ""Id_Registro"" IN (SELECT ""Id_Registro"" FROM ""Eventos_A_Registros"" WHERE ""Id_Usuario"" = @uid AND ""Id_Evento"" = @ev)";
                    using var cmd = new NpgsqlCommand(sql, con);
                    cmd.Parameters.AddWithValue("@sub", idSubtipo);
                    cmd.Parameters.AddWithValue("@uid", idUser);
                    cmd.Parameters.AddWithValue("@ev", idEvento);
                    await cmd.ExecuteNonQueryAsync();

                    // --- AVISO EN TIEMPO REAL (LUGAR LIBERADO) ---
                    await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("RefrescarMapa");
                }
                return Json(new { exito = true });
            }
            catch { return Json(new { exito = false }); }
        }
        private async Task<int> ObtenerOCrearRegistroPadre(int idEv, int idU, NpgsqlConnection con, NpgsqlTransaction tra)
        {
            string sql = @"SELECT ""Id_Registro"" FROM ""Eventos_A_Registros"" WHERE ""Id_Evento""=@ev AND ""Id_Usuario""=@u AND ""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes' LIMIT 1";
            using var cmd = new NpgsqlCommand(sql, con, tra);
            cmd.Parameters.AddWithValue("@ev", idEv); cmd.Parameters.AddWithValue("@u", idU);
            var res = await cmd.ExecuteScalarAsync();
            if (res != null) return (int)res;

            using var cmdI = new NpgsqlCommand(@"INSERT INTO ""Eventos_A_Registros"" (""Id_Evento"", ""Id_Usuario"", ""Fecha_Registro"") VALUES (@ev, @u, NOW()) RETURNING ""Id_Registro""", con, tra);
            cmdI.Parameters.AddWithValue("@ev", idEv); cmdI.Parameters.AddWithValue("@u", idU);
            return (int)await cmdI.ExecuteScalarAsync();
        }

        /// <summary>
        /// Realiza una limpieza física de los pre-registros '(PENDIENTE)' que han superado 
        /// el tiempo de gracia de 30 minutos, liberando cupos y limpiando la vista del usuario.
        /// </summary>
        private async Task LimpiarPendientesExpirados(int idEvento, int idUsuario, NpgsqlConnection conexion, NpgsqlTransaction trans = null)
        {
            // 1. Identificamos los asistentes que son '(PENDIENTE)' y cuya Fecha_Registro en Tabla A es mayor a 30 mins
            string sqlGetIds = @"
        SELECT b.""Id_Asistente"" 
        FROM ""Eventos_B_Asistentes"" b
        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
        WHERE r.""Id_Evento"" = @ev 
          AND r.""Id_Usuario"" = @usr 
          AND b.""Nombre_Completo"" = '(PENDIENTE)'
          AND r.""Fecha_Registro"" < NOW() - INTERVAL '30 minutes'";

            var idsABorrar = new List<int>();
            using (var cmdGet = new NpgsqlCommand(sqlGetIds, conexion, trans))
            {
                cmdGet.Parameters.AddWithValue("@ev", idEvento);
                cmdGet.Parameters.AddWithValue("@usr", idUsuario);
                using (var reader = await cmdGet.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync()) idsABorrar.Add(reader.GetInt32(0));
                }
            }

            if (idsABorrar.Any())
            {
                // 2. LIMPIEZA MANUAL EN CASCADA (Basado en la lógica de borrado de tu controlador) [cite: 310]

                // A. Borrar Deudas/Cuentas (Tabla C)
                string sqlDelC = @"DELETE FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = ANY(@ids)";
                using (var cmdC = new NpgsqlCommand(sqlDelC, conexion, trans))
                {
                    cmdC.Parameters.AddWithValue("@ids", idsABorrar.ToArray());
                    await cmdC.ExecuteNonQueryAsync();
                }

                // B. Borrar Respuestas (Tabla Eventos_Respuestas) [cite: 310]
                string sqlDelR = @"DELETE FROM ""Eventos_Respuestas"" WHERE ""Id_Asistente"" = ANY(@ids)";
                using (var cmdR = new NpgsqlCommand(sqlDelR, conexion, trans))
                {
                    cmdR.Parameters.AddWithValue("@ids", idsABorrar.ToArray());
                    await cmdR.ExecuteNonQueryAsync();
                }

                string sqlDelEncMasivo = @"
                    DELETE FROM ""Encuestas_Respuestas_Detalle"" 
                    WHERE ""Id_Respuesta"" IN (SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" WHERE ""Id_Asistente_Evento"" = ANY(@ids));
                    DELETE FROM ""Encuestas_Respuestas_Header"" 
                    WHERE ""Id_Asistente_Evento"" = ANY(@ids);";
                using (var cmdDelEnc = new NpgsqlCommand(sqlDelEncMasivo, conexion, trans))
                {
                    cmdDelEnc.Parameters.AddWithValue("@ids", idsABorrar.ToArray());
                    await cmdDelEnc.ExecuteNonQueryAsync();
                }

                // C1. Borrar Productos Extra del Asistente
                string sqlDelPE = @"DELETE FROM ""Eventos_Asistentes_ProductosExtra"" WHERE ""Id_Asistente"" = ANY(@ids)";
                using (var cmdPE = new NpgsqlCommand(sqlDelPE, conexion, trans))
                {
                    cmdPE.Parameters.AddWithValue("@ids", idsABorrar.ToArray());
                    await cmdPE.ExecuteNonQueryAsync();
                }

                // C2. Borrar los Asistentes (Tabla B) [cite: 310]
                string sqlDelB = @"DELETE FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                using (var cmdB = new NpgsqlCommand(sqlDelB, conexion, trans))
                {
                    cmdB.Parameters.AddWithValue("@ids", idsABorrar.ToArray());
                    await cmdB.ExecuteNonQueryAsync();
                }

                // 3. LIMPIEZA DE REGISTROS PADRE HUÉRFANOS (Tabla A)
                // Si después de borrar asistentes el registro (Id_Registro) se queda sin nadie, lo borramos. [cite: 310]
                string sqlDelA = @"
            DELETE FROM ""Eventos_A_Registros"" 
            WHERE ""Id_Evento"" = @ev 
              AND ""Id_Usuario"" = @usr
              AND NOT EXISTS (SELECT 1 FROM ""Eventos_B_Asistentes"" b WHERE b.""Id_Registro"" = ""Eventos_A_Registros"".""Id_Registro"")";

                using (var cmdA = new NpgsqlCommand(sqlDelA, conexion, trans))
                {
                    cmdA.Parameters.AddWithValue("@ev", idEvento);
                    cmdA.Parameters.AddWithValue("@usr", idUsuario);
                    await cmdA.ExecuteNonQueryAsync();
                }
            }
        }


        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ApiGetEstadisticasModalidad(int idEvento, int idSubtipo)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var statsH = new StatGeneroInfo();
                    var statsM = new StatGeneroInfo();
                    var statsX = new StatGeneroInfo();

                    string nombre = "";
                    string cupoBD = "∞";

                    // 1. Obtener Nombre y Cupo Global
                    string sqlCupo = @"SELECT ""Nombre"", ""Cupo"" FROM ""Eventos_Subtipos"" WHERE ""Id_Subtipo"" = @sub";
                    using (var cmd = new NpgsqlCommand(sqlCupo, conexion))
                    {
                        cmd.Parameters.AddWithValue("@sub", idSubtipo);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                nombre = "🔹 " + r["Nombre"].ToString();
                                int c = Convert.ToInt32(r["Cupo"]);
                                cupoBD = c > 0 ? c.ToString() : "∞";
                            }
                        }
                    }

                    // 2. Separar Espacios por Género del Cuarto
                    string sqlEspacios = @"
                SELECT g.""Genero_Asignado"", SUM(h.""Capacidad_Total"") as ""Total""
                FROM ""Eventos_H_Alojamiento_Capacidades"" h
                JOIN ""Eventos_G_Alojamientos"" g ON h.""Id_Alojamiento"" = g.""Id_Alojamiento""
                JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                WHERE z.""Id_Evento"" = @ev AND h.""Id_Subtipo"" = @sub
                GROUP BY g.""Genero_Asignado""";

                    using (var cmd = new NpgsqlCommand(sqlEspacios, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        cmd.Parameters.AddWithValue("@sub", idSubtipo);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string gen = r["Genero_Asignado"]?.ToString();
                                int tot = Convert.ToInt32(r["Total"]);
                                if (gen == "H") statsH.espacios += tot;
                                else if (gen == "M") statsM.espacios += tot;
                                else statsX.espacios += tot;
                            }
                        }
                    }

                    // 3. Procesar a las Personas
                    string sqlGente = @"
                SELECT 
                    b.""Nombre_Completo"",
                    b.""Genero"",
                    s.""Nombre"" as ""Subtipo"",
                    (CASE WHEN asig.""Id_Asignacion"" IS NOT NULL THEN 1 ELSE 0 END) as ""Asignado"",
                    g.""Genero_Asignado"" as ""GeneroCuarto"",
                    (CASE WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 1 ELSE 0 END) as ""Pagado"",
                    CASE 
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 'Pagado'
                        ELSE 'Pago Incompleto'
                    END as ""Estado_Financiero"",
                    CASE 
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 'Lugar asegurado.'
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND tx.""Estatus_Pago"" = 'review') THEN 'Comprobante en revisión.'
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND tx.""Estatus_Pago"" = 'waiting_proof') THEN 'Falta subir comprobante.'
                        WHEN r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes' THEN 'Registro o checkout en proceso...'
                        ELSE 'Intento abandonado / Expirado.'
                    END as ""Detalle_Diagnostico"",
                    (CASE WHEN (
                        b.""Es_Pagado"" = TRUE
                        OR r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes'
                        OR EXISTS (
                            SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx 
                            JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" 
                            JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" 
                            WHERE cx.""Id_Asistente"" = b.""Id_Asistente""
                            AND (tx.""Estatus_Pago"" IN ('paid', 'processing', 'waiting_proof', 'review') 
                                 OR tx.""EsTransferencia"" = TRUE 
                                 OR (tx.""Estatus_Pago"" = 'pending' AND tx.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes'))
                        )
                    ) THEN 1 ELSE 0 END) as ""EsValido""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_I_Alojamiento_Asignaciones"" asig ON b.""Id_Asistente"" = asig.""Id_Asistente""
                LEFT JOIN ""Eventos_G_Alojamientos"" g ON asig.""Id_Alojamiento"" = g.""Id_Alojamiento""
                WHERE r.""Id_Evento"" = @ev AND b.""Id_Subtipo"" = @sub
                ORDER BY ""Estado_Financiero"" DESC, b.""Nombre_Completo"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlGente, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        cmd.Parameters.AddWithValue("@sub", idSubtipo);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                if (Convert.ToInt32(r["EsValido"]) == 0) continue;

                                string genPersona = r["Genero"].ToString();
                                int asignado = Convert.ToInt32(r["Asignado"]);
                                string genCuarto = r["GeneroCuarto"]?.ToString();
                                int pagado = Convert.ToInt32(r["Pagado"]);

                                var obj = new
                                {
                                    nombre = r["Nombre_Completo"].ToString(),
                                    estado = r["Estado_Financiero"].ToString(),
                                    diagnostico = $"[{r["Subtipo"]}] " + r["Detalle_Diagnostico"].ToString(),
                                    genero = genPersona
                                };

                                if (asignado == 1)
                                {
                                    if (genCuarto == "H") { statsH.ocupados++; statsH.lista.Add(obj); }
                                    else if (genCuarto == "M") { statsM.ocupados++; statsM.lista.Add(obj); }
                                    else { statsX.ocupados++; statsX.lista.Add(obj); }
                                }
                                else
                                {
                                    if (pagado == 1)
                                    {
                                        if (genPersona == "H") { statsH.porAsignar++; statsH.lista.Add(obj); }
                                        else { statsM.porAsignar++; statsM.lista.Add(obj); }
                                    }
                                    else
                                    {
                                        if (genPersona == "H") { statsH.enProceso++; statsH.lista.Add(obj); }
                                        else { statsM.enProceso++; statsM.lista.Add(obj); }
                                    }
                                }
                            }
                        }
                    }

                    return Json(new { exito = true, titulo = nombre, cupoBD = cupoBD, h = statsH, m = statsM, x = statsX });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ApiGetEstadisticasAgrupacion(int idEvento, int idAgrupacion)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var statsH = new StatGeneroInfo();
                    var statsM = new StatGeneroInfo();
                    var statsX = new StatGeneroInfo();

                    string nombre = "";
                    string cupoBD = "∞";

                    string sqlCupo = @"
                SELECT a.""Nombre_Descriptivo"", 
                (SELECT COALESCE(SUM(s.""Cupo""), 0) FROM ""Eventos_Subtipos"" s JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON s.""Id_Subtipo"" = amd.""Id_Subtipo"" WHERE amd.""Id_Agrupacion"" = a.""Id_Agrupacion"") as ""Cupo""
                FROM ""Eventos_K_AgrupaModalidades"" a WHERE a.""Id_Agrupacion"" = @agr";

                    using (var cmd = new NpgsqlCommand(sqlCupo, conexion))
                    {
                        cmd.Parameters.AddWithValue("@agr", idAgrupacion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                nombre = "⭐ " + r["Nombre_Descriptivo"].ToString();
                                int c = Convert.ToInt32(r["Cupo"]);
                                cupoBD = c > 0 ? c.ToString() : "∞";
                            }
                        }
                    }

                    string sqlEspacios = @"
                SELECT g.""Genero_Asignado"", SUM(h.""Capacidad_Total"") as ""Total""
                FROM ""Eventos_H_Alojamiento_Capacidades"" h
                JOIN ""Eventos_G_Alojamientos"" g ON h.""Id_Alojamiento"" = g.""Id_Alojamiento""
                JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                WHERE z.""Id_Evento"" = @ev AND h.""Id_Agrupacion"" = @agr
                GROUP BY g.""Genero_Asignado""";

                    using (var cmd = new NpgsqlCommand(sqlEspacios, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        cmd.Parameters.AddWithValue("@agr", idAgrupacion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string gen = r["Genero_Asignado"]?.ToString();
                                int tot = Convert.ToInt32(r["Total"]);
                                if (gen == "H") statsH.espacios += tot;
                                else if (gen == "M") statsM.espacios += tot;
                                else statsX.espacios += tot;
                            }
                        }
                    }

                    string sqlGente = @"
                SELECT 
                    b.""Nombre_Completo"",
                    b.""Genero"",
                    s.""Nombre"" as ""Subtipo"",
                    (CASE WHEN asig.""Id_Asignacion"" IS NOT NULL THEN 1 ELSE 0 END) as ""Asignado"",
                    g.""Genero_Asignado"" as ""GeneroCuarto"",
                    (CASE WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 1 ELSE 0 END) as ""Pagado"",
                    CASE 
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 'Pagado'
                        ELSE 'Pago Incompleto'
                    END as ""Estado_Financiero"",
                    CASE 
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) THEN 'Lugar asegurado.'
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND tx.""Estatus_Pago"" = 'review') THEN 'Comprobante en revisión.'
                        WHEN EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND tx.""Estatus_Pago"" = 'waiting_proof') THEN 'Falta subir comprobante.'
                        WHEN r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes' THEN 'Registro o checkout en proceso...'
                        ELSE 'Intento abandonado / Expirado.'
                    END as ""Detalle_Diagnostico"",
                    (CASE WHEN (
                        b.""Es_Pagado"" = TRUE
                        OR r.""Fecha_Registro"" >= NOW() - INTERVAL '30 minutes'
                        OR EXISTS (
                            SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx 
                            JOIN ""Eventos_E_Pagos_Aplicados"" px ON cx.""Id_Cuenta"" = px.""Id_Cuenta"" 
                            JOIN ""Eventos_D_Transacciones"" tx ON px.""Id_Transaccion"" = tx.""Id_Transaccion"" 
                            WHERE cx.""Id_Asistente"" = b.""Id_Asistente""
                            AND (tx.""Estatus_Pago"" IN ('paid', 'processing', 'waiting_proof', 'review') 
                                 OR tx.""EsTransferencia"" = TRUE 
                                 OR (tx.""Estatus_Pago"" = 'pending' AND tx.""Fecha_Intento"" >= NOW() - INTERVAL '30 minutes'))
                        )
                    ) THEN 1 ELSE 0 END) as ""EsValido""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON b.""Id_Subtipo"" = amd.""Id_Subtipo""
                LEFT JOIN ""Eventos_I_Alojamiento_Asignaciones"" asig ON b.""Id_Asistente"" = asig.""Id_Asistente""
                LEFT JOIN ""Eventos_G_Alojamientos"" g ON asig.""Id_Alojamiento"" = g.""Id_Alojamiento""
                WHERE r.""Id_Evento"" = @ev AND amd.""Id_Agrupacion"" = @agr
                ORDER BY ""Estado_Financiero"" DESC, b.""Nombre_Completo"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlGente, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", idEvento);
                        cmd.Parameters.AddWithValue("@agr", idAgrupacion);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                if (Convert.ToInt32(r["EsValido"]) == 0) continue;

                                string genPersona = r["Genero"].ToString();
                                int asignado = Convert.ToInt32(r["Asignado"]);
                                string genCuarto = r["GeneroCuarto"]?.ToString();
                                int pagado = Convert.ToInt32(r["Pagado"]);

                                var obj = new
                                {
                                    nombre = r["Nombre_Completo"].ToString(),
                                    estado = r["Estado_Financiero"].ToString(),
                                    diagnostico = $"[{r["Subtipo"]}] " + r["Detalle_Diagnostico"].ToString(),
                                    genero = genPersona
                                };

                                if (asignado == 1)
                                {
                                    if (genCuarto == "H") { statsH.ocupados++; statsH.lista.Add(obj); }
                                    else if (genCuarto == "M") { statsM.ocupados++; statsM.lista.Add(obj); }
                                    else { statsX.ocupados++; statsX.lista.Add(obj); }
                                }
                                else
                                {
                                    if (pagado == 1)
                                    {
                                        if (genPersona == "H") { statsH.porAsignar++; statsH.lista.Add(obj); }
                                        else { statsM.porAsignar++; statsM.lista.Add(obj); }
                                    }
                                    else
                                    {
                                        if (genPersona == "H") { statsH.enProceso++; statsH.lista.Add(obj); }
                                        else { statsM.enProceso++; statsM.lista.Add(obj); }
                                    }
                                }
                            }
                        }
                    }

                    return Json(new { exito = true, titulo = nombre, cupoBD = cupoBD, h = statsH, m = statsM, x = statsX });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Activa o desactiva la publicación de una habitación para el portal auto-servicio.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiToggleAlojamientoPortal(int idAlojamiento, bool publicar)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "No tienes permisos." });

            int idEventoDetectado = 0;
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Lógica UPSERT
                    string sql = @"
                INSERT INTO ""Eventos_K_Alojamientos_Publicados"" (""Id_Alojamiento"", ""Activo"") 
                VALUES (@id, @act) 
                ON CONFLICT (""Id_Alojamiento"") 
                DO UPDATE SET ""Activo"" = EXCLUDED.""Activo""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idAlojamiento);
                        cmd.Parameters.AddWithValue("@act", publicar);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Rescatar IdEvento para notificar
                    using (var cmdE = new NpgsqlCommand(@"SELECT z.""Id_Evento"" FROM ""Eventos_G_Alojamientos"" g JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona"" WHERE g.""Id_Alojamiento"" = @id", conexion))
                    {
                        cmdE.Parameters.AddWithValue("@id", idAlojamiento);
                        var resEv = await cmdE.ExecuteScalarAsync();
                        if (resEv != null) idEventoDetectado = (int)resEv;
                    }
                }

                // NOTIFICAR A TODOS
                if (idEventoDetectado > 0)
                {
                    await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                }

                return Json(new { exito = true });
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Guarda la configuración global del portal (Sin mensaje de cierre).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiGuardarConfigPortal([FromBody] ConfigPortalRequest req)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "No tienes permisos." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"
                INSERT INTO ""Eventos_ConfigAlojamiento"" (""Id_Evento"", ""Portal_Activo"", ""Solo_Pagados"", ""Mostrar_Todas_Habitaciones"") 
                VALUES (@ev, @act, @solo, @todas) 
                ON CONFLICT (""Id_Evento"") 
                DO UPDATE SET ""Portal_Activo"" = EXCLUDED.""Portal_Activo"", 
                              ""Solo_Pagados"" = EXCLUDED.""Solo_Pagados"",
                              ""Mostrar_Todas_Habitaciones"" = EXCLUDED.""Mostrar_Todas_Habitaciones""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@ev", req.IdEvento);
                        cmd.Parameters.AddWithValue("@act", req.PortalActivo);
                        cmd.Parameters.AddWithValue("@solo", req.SoloPagados);
                        cmd.Parameters.AddWithValue("@todas", req.MostrarTodasLasHabitaciones);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                return Json(new { exito = true });
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Crea una nueva agrupación de modalidades.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiCrearAgrupacion([FromBody] CrearAgrupacionRequest req)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "No tienes permisos." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Generamos una clave técnica automática sin espacios ni caracteres raros
                        string claveTecnica = System.Text.RegularExpressions.Regex.Replace(req.Nombre.ToUpper().Trim(), "[^A-Z0-9]", "_");

                        var cmd = new NpgsqlCommand(@"INSERT INTO ""Eventos_K_AgrupaModalidades"" (""Id_Evento"", ""Clave_Tecnica"", ""Nombre_Descriptivo"") VALUES (@ev, @clave, @nom) RETURNING ""Id_Agrupacion""", conexion, trans);
                        cmd.Parameters.AddWithValue("@ev", req.IdEvento);
                        cmd.Parameters.AddWithValue("@clave", claveTecnica);
                        cmd.Parameters.AddWithValue("@nom", req.Nombre);

                        int idAgrupacion = (int)await cmd.ExecuteScalarAsync();

                        foreach (int subId in req.Subtipos)
                        {
                            var cmdDet = new NpgsqlCommand(@"INSERT INTO ""Eventos_K_AgrupaModalidadesDetalle"" (""Id_Agrupacion"", ""Id_Subtipo"") VALUES (@ag, @sub)", conexion, trans);
                            cmdDet.Parameters.AddWithValue("@ag", idAgrupacion);
                            cmdDet.Parameters.AddWithValue("@sub", subId);
                            await cmdDet.ExecuteNonQueryAsync();
                        }

                        await trans.CommitAsync();
                        return Json(new { exito = true });
                    }
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX: Elimina una agrupación de modalidades (Borrado en cascada).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ApiEliminarAgrupacion(int idAgrupacion)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "No tienes permisos." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Al borrar el padre, PostgreSQL borrará automáticamente los detalles puente 
                    // y las capacidades en Eventos_H_Alojamiento_Capacidades gracias al ON DELETE CASCADE.
                    var cmd = new NpgsqlCommand(@"DELETE FROM ""Eventos_K_AgrupaModalidades"" WHERE ""Id_Agrupacion"" = @id", conexion);
                    cmd.Parameters.AddWithValue("@id", idAgrupacion);
                    await cmd.ExecuteNonQueryAsync();
                }
                return Json(new { exito = true });
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// AJAX (Lightweight): Devuelve únicamente la lista actualizada de habitaciones y su cupo.
        /// Usado por SignalR para reactividad en tiempo real sin recargar toda la página.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ApiGetHabitacionesDisponibles(int idEvento)
        {
            var habitaciones = new List<HabitacionDisponiblePortal>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlCuartos = @"
                SELECT g.""Id_Alojamiento"", g.""Nombre"" as ""Habitacion"", z.""Nombre"" as ""Zona"", g.""Genero_Asignado""
                FROM ""Eventos_G_Alojamientos"" g
                JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                JOIN ""Eventos_K_Alojamientos_Publicados"" p ON g.""Id_Alojamiento"" = p.""Id_Alojamiento""
                WHERE z.""Id_Evento"" = @idEv AND p.""Activo"" = TRUE
                AND g.""Genero_Asignado"" IN ('H', 'M')
                ORDER BY z.""Nombre"" ASC, g.""Nombre"" ASC";

                    using (var cmdC = new NpgsqlCommand(sqlCuartos, conexion))
                    {
                        cmdC.Parameters.AddWithValue("@idEv", idEvento);
                        using (var r = await cmdC.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                habitaciones.Add(new HabitacionDisponiblePortal
                                {
                                    IdAlojamiento = (int)r["Id_Alojamiento"],
                                    NombreHabitacion = r["Habitacion"].ToString(),
                                    NombreZona = r["Zona"].ToString(),
                                    GeneroAsignado = r["Genero_Asignado"]?.ToString()
                                });
                            }
                        }
                    }

                    foreach (var hab in habitaciones)
                    {
                        string sqlCaps = @"
                    SELECT c.""Capacidad_Total"",
                           CASE WHEN c.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || c.""Id_Agrupacion"" ELSE 'SUB_' || c.""Id_Subtipo"" END as ""RefTipo"",
                           COALESCE(ag.""Nombre_Descriptivo"", s.""Nombre"", 'General') as ""NombreTipo""
                    FROM ""Eventos_H_Alojamiento_Capacidades"" c
                    LEFT JOIN ""Eventos_Subtipos"" s ON c.""Id_Subtipo"" = s.""Id_Subtipo""
                    LEFT JOIN ""Eventos_K_AgrupaModalidades"" ag ON c.""Id_Agrupacion"" = ag.""Id_Agrupacion""
                    WHERE c.""Id_Alojamiento"" = @idA";

                        using (var cmdCap = new NpgsqlCommand(sqlCaps, conexion))
                        {
                            cmdCap.Parameters.AddWithValue("@idA", hab.IdAlojamiento);
                            using (var r = await cmdCap.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    hab.Capacidades.Add(new CapacidadPortal
                                    {
                                        RefTipo = r["RefTipo"].ToString(),
                                        NombreTipo = r["NombreTipo"].ToString(),
                                        CapacidadTotal = (int)r["Capacidad_Total"]
                                    });
                                }
                            }
                        }

                        string sqlOcu = @"
                    SELECT b.""Genero"",
                           CASE WHEN amd.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || amd.""Id_Agrupacion"" ELSE 'SUB_' || b.""Id_Subtipo"" END as ""RefTipo""
                    FROM ""Eventos_I_Alojamiento_Asignaciones"" a
                    JOIN ""Eventos_B_Asistentes"" b ON a.""Id_Asistente"" = b.""Id_Asistente""
                    LEFT JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON b.""Id_Subtipo"" = amd.""Id_Subtipo""
                    WHERE a.""Id_Alojamiento"" = @idA";

                        using (var cmdOcu = new NpgsqlCommand(sqlOcu, conexion))
                        {
                            cmdOcu.Parameters.AddWithValue("@idA", hab.IdAlojamiento);
                            using (var r = await cmdOcu.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    string refTipo = r["RefTipo"].ToString();
                                    string genero = r["Genero"].ToString();

                                    var cap = hab.Capacidades.FirstOrDefault(c => c.RefTipo == refTipo);
                                    if (cap != null)
                                    {
                                        cap.LugaresOcupados++;
                                        cap.GenerosOcupantes.Add(genero);
                                    }
                                }
                            }
                        }
                    }
                }

                // Retornamos el Content explícito serializado para garantizar que las 
                // mayúsculas/minúsculas sean IDÉNTICAS al JSON inyectado al cargar la vista.
                return Content(System.Text.Json.JsonSerializer.Serialize(habitaciones), "application/json");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// GET: Muestra el portal de selección de habitaciones al usuario.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ElegirAlojamiento(string sid)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new PortalAlojamientoViewModel();

            try
            {
                int idEvento = Funciones.DesencriptarId(sid);
                modelo.IdEvento = idEvento;
                modelo.IdEventoEncriptado = sid;

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDAR ACCESO AL EVENTO Y DATOS BÁSICOS
                    if (!await ValidarAccesoAEvento(idEvento, idUser, conexion))
                    {
                        MostrarMensaje("Acceso Denegado", "No tienes registro en este evento.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        modelo.TituloEvento = (string)await cmdEv.ExecuteScalarAsync();
                    }

                    // 2. CONFIGURACIÓN DEL PORTAL
                    bool soloPagados = false;
                    using (var cmdConf = new NpgsqlCommand(@"SELECT ""Portal_Activo"", ""Solo_Pagados"", ""Mostrar_Todas_Habitaciones"" FROM ""Eventos_ConfigAlojamiento"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdConf.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmdConf.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.PortalActivo = (bool)r["Portal_Activo"];
                                soloPagados = (bool)r["Solo_Pagados"];
                                modelo.MostrarTodasLasHabitaciones = r["Mostrar_Todas_Habitaciones"] != DBNull.Value && (bool)r["Mostrar_Todas_Habitaciones"];
                            }
                        }
                    }

                    if (!modelo.PortalActivo)
                    {
                        modelo.MensajeInactivo = "La selección de habitaciones aún no está habilitada o ha sido cerrada por los organizadores.";
                        return View(modelo);
                    }

                    // Establecemos si la regla de "Solo Pagados" está activa para que la vista condicione a cada persona
                    modelo.BloqueoPorDeuda = soloPagados;

                    // 3. CARGAR MI GRUPO
                    string sqlGrupo = @"
                SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Genero"", b.""Id_Subtipo"",
                       COALESCE(ag.""Nombre_Descriptivo"", s.""Nombre"", 'Entrada General') as ""TipoAlojamiento"",
                       CASE WHEN amd.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || amd.""Id_Agrupacion"" ELSE 'SUB_' || b.""Id_Subtipo"" END as ""RefTipo"",
                       a.""Id_Alojamiento"", g.""Nombre"" as ""Habitacion"", z.""Nombre"" as ""Zona"",
                       (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE) as ""Deudas""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON b.""Id_Subtipo"" = amd.""Id_Subtipo""
                LEFT JOIN ""Eventos_K_AgrupaModalidades"" ag ON amd.""Id_Agrupacion"" = ag.""Id_Agrupacion""
                LEFT JOIN ""Eventos_I_Alojamiento_Asignaciones"" a ON b.""Id_Asistente"" = a.""Id_Asistente""
                LEFT JOIN ""Eventos_G_Alojamientos"" g ON a.""Id_Alojamiento"" = g.""Id_Alojamiento""
                LEFT JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                WHERE r.""Id_Evento"" = @idEv AND r.""Id_Usuario"" = @idUsr
                ORDER BY b.""Nombre_Completo"" ASC";

                    using (var cmdG = new NpgsqlCommand(sqlGrupo, conexion))
                    {
                        cmdG.Parameters.AddWithValue("@idEv", idEvento);
                        cmdG.Parameters.AddWithValue("@idUsr", idUser);
                        using (var r = await cmdG.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int deudas = Convert.ToInt32(r["Deudas"]);

                                modelo.MiGrupo.Add(new MiembroGrupoAlojamiento
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Genero = r["Genero"].ToString(),
                                    IdSubtipo = r["Id_Subtipo"] != DBNull.Value ? (int?)r["Id_Subtipo"] : null,
                                    TipoAlojamiento = r["TipoAlojamiento"].ToString(),
                                    RefTipo = r["RefTipo"].ToString(),
                                    TieneDeuda = deudas > 0, // Individualmente marcamos si debe
                                    IdAlojamientoActual = r["Id_Alojamiento"] != DBNull.Value ? (int?)r["Id_Alojamiento"] : null,
                                    NombreHabitacionActual = r["Habitacion"]?.ToString(),
                                    NombreZonaActual = r["Zona"]?.ToString()
                                });
                            }
                        }
                    }

                    // 4. CARGAR HABITACIONES PUBLICADAS (REGLA ESTRICTA DE GÉNERO)
                    string sqlCuartos = @"
                    SELECT g.""Id_Alojamiento"", g.""Nombre"" as ""Habitacion"", z.""Nombre"" as ""Zona"", g.""Genero_Asignado""
                    FROM ""Eventos_G_Alojamientos"" g
                    JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                    JOIN ""Eventos_K_Alojamientos_Publicados"" p ON g.""Id_Alojamiento"" = p.""Id_Alojamiento""
                    WHERE z.""Id_Evento"" = @idEv AND p.""Activo"" = TRUE
                    AND g.""Genero_Asignado"" IN ('H', 'M') -- REGLA ESTRICTA: Solo cuartos con género H o M
                    ORDER BY z.""Nombre"" ASC, g.""Nombre"" ASC";

                    using (var cmdC = new NpgsqlCommand(sqlCuartos, conexion))
                    {
                        cmdC.Parameters.AddWithValue("@idEv", idEvento);
                        using (var r = await cmdC.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Habitaciones.Add(new HabitacionDisponiblePortal
                                {
                                    IdAlojamiento = (int)r["Id_Alojamiento"],
                                    NombreHabitacion = r["Habitacion"].ToString(),
                                    NombreZona = r["Zona"].ToString(),
                                    GeneroAsignado = r["Genero_Asignado"]?.ToString()
                                });
                            }
                        }
                    }

                    foreach (var hab in modelo.Habitaciones)
                    {
                        string sqlCaps = @"
                    SELECT c.""Capacidad_Total"",
                           CASE WHEN c.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || c.""Id_Agrupacion"" ELSE 'SUB_' || c.""Id_Subtipo"" END as ""RefTipo"",
                           COALESCE(ag.""Nombre_Descriptivo"", s.""Nombre"", 'General') as ""NombreTipo""
                    FROM ""Eventos_H_Alojamiento_Capacidades"" c
                    LEFT JOIN ""Eventos_Subtipos"" s ON c.""Id_Subtipo"" = s.""Id_Subtipo""
                    LEFT JOIN ""Eventos_K_AgrupaModalidades"" ag ON c.""Id_Agrupacion"" = ag.""Id_Agrupacion""
                    WHERE c.""Id_Alojamiento"" = @idA";

                        using (var cmdCap = new NpgsqlCommand(sqlCaps, conexion))
                        {
                            cmdCap.Parameters.AddWithValue("@idA", hab.IdAlojamiento);
                            using (var r = await cmdCap.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    hab.Capacidades.Add(new CapacidadPortal
                                    {
                                        RefTipo = r["RefTipo"].ToString(),
                                        NombreTipo = r["NombreTipo"].ToString(),
                                        CapacidadTotal = (int)r["Capacidad_Total"]
                                    });
                                }
                            }
                        }

                        string sqlOcu = @"
                    SELECT b.""Genero"",
                           CASE WHEN amd.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || amd.""Id_Agrupacion"" ELSE 'SUB_' || b.""Id_Subtipo"" END as ""RefTipo""
                    FROM ""Eventos_I_Alojamiento_Asignaciones"" a
                    JOIN ""Eventos_B_Asistentes"" b ON a.""Id_Asistente"" = b.""Id_Asistente""
                    LEFT JOIN ""Eventos_K_AgrupaModalidadesDetalle"" amd ON b.""Id_Subtipo"" = amd.""Id_Subtipo""
                    WHERE a.""Id_Alojamiento"" = @idA";

                        using (var cmdOcu = new NpgsqlCommand(sqlOcu, conexion))
                        {
                            cmdOcu.Parameters.AddWithValue("@idA", hab.IdAlojamiento);
                            using (var r = await cmdOcu.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    string refTipo = r["RefTipo"].ToString();
                                    string genero = r["Genero"].ToString();

                                    var cap = hab.Capacidades.FirstOrDefault(c => c.RefTipo == refTipo);
                                    if (cap != null)
                                    {
                                        cap.LugaresOcupados++;
                                        cap.GenerosOcupantes.Add(genero);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Problema al cargar el portal: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        /// <summary>
        /// POST (AJAX): Guarda la selección del usuario. Maneja asignaciones individuales o múltiples combinadas.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApiAsignarHabitacionPortal([FromBody] List<AsignacionPortalRequest> reqs)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            if (reqs == null || !reqs.Any())
                return Json(new { exito = false, mensaje = "Datos incompletos." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. EXTRAER TODOS LOS IDS PARA VALIDACIÓN
                        var todosAsistentesIds = reqs.SelectMany(r => r.IdsAsistentes).Distinct().ToList();
                        var todasHabitacionesIds = reqs.Select(r => r.IdAlojamiento).Distinct().OrderBy(id => id).ToList();

                        // 2. VALIDACIÓN DE PROPIEDAD
                        string sqlProp = @"SELECT COUNT(*) FROM ""Eventos_B_Asistentes"" b JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" WHERE b.""Id_Asistente"" = ANY(@ids) AND r.""Id_Usuario"" = @uid";
                        using (var cmdP = new NpgsqlCommand(sqlProp, conexion, trans))
                        {
                            cmdP.Parameters.AddWithValue("@ids", todosAsistentesIds);
                            cmdP.Parameters.AddWithValue("@uid", idUser);
                            if ((long)await cmdP.ExecuteScalarAsync() != todosAsistentesIds.Count)
                                throw new Exception("Intento de asignar personas que no pertenecen a tu registro.");
                        }

                        // 3. VALIDACIÓN ESTRICTA DE GÉNERO MIXTO EN LA SOLICITUD (BACKEND)
                        string sqlGen = @"SELECT DISTINCT ""Genero"" FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = ANY(@ids)";
                        var generosGrupo = new List<string>();
                        using (var cmdG = new NpgsqlCommand(sqlGen, conexion, trans))
                        {
                            cmdG.Parameters.AddWithValue("@ids", todosAsistentesIds);
                            using (var r = await cmdG.ExecuteReaderAsync()) while (await r.ReadAsync()) generosGrupo.Add(r[0].ToString());
                        }

                        if (generosGrupo.Count > 1)
                            throw new Exception("Violación de seguridad: No se permiten selecciones con géneros mixtos.");

                        string generoGrupoFinal = generosGrupo.First();
                        if (generoGrupoFinal != "H" && generoGrupoFinal != "M")
                            throw new Exception("Violación de seguridad: Género no válido.");

                        // 4. BLOQUEO DE HABITACIONES FOR UPDATE (EN ORDEN PARA EVITAR DEADLOCKS)
                        string sqlValCuarto = @"
                        SELECT g.""Id_Alojamiento"", g.""Genero_Asignado"", c.""Capacidad_Total"", z.""Id_Evento"",
                               CASE WHEN c.""Id_Agrupacion"" IS NOT NULL THEN 'AGR_' || c.""Id_Agrupacion"" ELSE 'SUB_' || c.""Id_Subtipo"" END as ""RefTipo"",
                               (SELECT COUNT(*) FROM ""Eventos_I_Alojamiento_Asignaciones"" a2 
                                JOIN ""Eventos_B_Asistentes"" b2 ON a2.""Id_Asistente"" = b2.""Id_Asistente"" 
                                WHERE a2.""Id_Alojamiento"" = g.""Id_Alojamiento"" 
                                AND (
                                    (c.""Id_Subtipo"" IS NOT NULL AND b2.""Id_Subtipo"" = c.""Id_Subtipo"") OR 
                                    (c.""Id_Agrupacion"" IS NOT NULL AND c.""Id_Agrupacion"" IN (SELECT ""Id_Agrupacion"" FROM ""Eventos_K_AgrupaModalidadesDetalle"" WHERE ""Id_Subtipo"" = b2.""Id_Subtipo""))
                                )
                               ) as ""Ocupados""
                        FROM ""Eventos_G_Alojamientos"" g
                        JOIN ""Eventos_F_Zonas"" z ON g.""Id_Zona"" = z.""Id_Zona""
                        JOIN ""Eventos_K_Alojamientos_Publicados"" p ON g.""Id_Alojamiento"" = p.""Id_Alojamiento""
                        JOIN ""Eventos_H_Alojamiento_Capacidades"" c ON g.""Id_Alojamiento"" = c.""Id_Alojamiento""
                        WHERE g.""Id_Alojamiento"" = ANY(@idsAloj) AND p.""Activo"" = TRUE 
                        ORDER BY g.""Id_Alojamiento"" FOR UPDATE OF g";

                        var cuartosData = new Dictionary<int, (string Genero, Dictionary<string, (int Max, int Ocupados)> Caps)>();

                        int idEventoDetectado = 0;
                        using (var cmdV = new NpgsqlCommand(sqlValCuarto, conexion, trans))
                        {
                            cmdV.Parameters.AddWithValue("@idsAloj", todasHabitacionesIds);
                            using (var r = await cmdV.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    if (idEventoDetectado == 0) idEventoDetectado = (int)r["Id_Evento"];
                                    int idAloj = (int)r["Id_Alojamiento"];
                                    string genero = r["Genero_Asignado"]?.ToString();
                                    string refTipo = r["RefTipo"].ToString();
                                    int max = (int)r["Capacidad_Total"];
                                    int ocupados = Convert.ToInt32(r["Ocupados"]);

                                    if (!cuartosData.ContainsKey(idAloj))
                                        cuartosData[idAloj] = (genero, new Dictionary<string, (int, int)>());

                                    cuartosData[idAloj].Caps[refTipo] = (max, ocupados);
                                }
                            }
                        }

                        // 5. OBTENER DATOS REALES Y ASIGNACIONES PREVIAS (SEGURIDAD ZERO-TRUST)
                        string sqlAsistentesData = @"
    SELECT b.""Id_Asistente"", b.""Id_Subtipo"",
           (SELECT d.""Id_Agrupacion"" FROM ""Eventos_K_AgrupaModalidadesDetalle"" d WHERE d.""Id_Subtipo"" = b.""Id_Subtipo"" LIMIT 1) as ""Id_Agrupacion"",
           (SELECT a.""Id_Alojamiento"" FROM ""Eventos_I_Alojamiento_Asignaciones"" a WHERE a.""Id_Asistente"" = b.""Id_Asistente"" LIMIT 1) as ""IdAlojamientoActual""
    FROM ""Eventos_B_Asistentes"" b WHERE b.""Id_Asistente"" = ANY(@ids)";

                        var asisInfo = new Dictionary<int, (int Subtipo, int? Agrupacion, int? IdAlojamientoActual)>();
                        using (var cmdA = new NpgsqlCommand(sqlAsistentesData, conexion, trans))
                        {
                            cmdA.Parameters.AddWithValue("@ids", todosAsistentesIds);
                            using (var r = await cmdA.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    asisInfo[(int)r["Id_Asistente"]] = (
                                        (int)r["Id_Subtipo"],
                                        r["Id_Agrupacion"] != DBNull.Value ? (int?)r["Id_Agrupacion"] : null,
                                        r["IdAlojamientoActual"] != DBNull.Value ? (int?)r["IdAlojamientoActual"] : null
                                    );
                                }
                            }
                        }

                        // 6. VALIDAR CADA REQUEST EN MEMORIA (CUPOS Y COMPATIBILIDAD)
                        foreach (var req in reqs)
                        {
                            if (!cuartosData.ContainsKey(req.IdAlojamiento))
                                throw new Exception($"La habitación seleccionada ya no está publicada o fue eliminada.");

                            var cuarto = cuartosData[req.IdAlojamiento];

                            if (string.IsNullOrEmpty(cuarto.Genero) || (cuarto.Genero != "H" && cuarto.Genero != "M"))
                                throw new Exception("Una de las habitaciones no tiene un género válido definido.");

                            if (cuarto.Genero != generoGrupoFinal)
                                throw new Exception($"El género del grupo ({generoGrupoFinal}) no coincide con la habitación ({cuarto.Genero}).");

                            // Agrupamos y contamos a los asistentes reales de esta request
                            var conteoNuevosPorRefTipo = new Dictionary<string, int>();

                            foreach (int idAsis in req.IdsAsistentes)
                            {
                                var info = asisInfo[idAsis];

                                // Si el usuario YA estaba asignado a esta misma habitación, no lo sumamos como "nuevo"
                                // porque la subquery 'Ocupados' en base de datos ya lo contó
                                if (info.IdAlojamientoActual == req.IdAlojamiento)
                                    continue;

                                string refTipoConsumido = null;
                                string keySub = $"SUB_{info.Subtipo}";
                                string keyAgr = info.Agrupacion.HasValue ? $"AGR_{info.Agrupacion}" : null;

                                // Identificamos qué capacidad real va a consumir en la habitación
                                if (cuarto.Caps.ContainsKey(keySub)) refTipoConsumido = keySub;
                                else if (keyAgr != null && cuarto.Caps.ContainsKey(keyAgr)) refTipoConsumido = keyAgr;

                                if (refTipoConsumido == null)
                                    throw new Exception("Uno de los miembros de tu grupo no tiene una modalidad compatible con esta habitación.");

                                if (!conteoNuevosPorRefTipo.ContainsKey(refTipoConsumido)) conteoNuevosPorRefTipo[refTipoConsumido] = 0;
                                conteoNuevosPorRefTipo[refTipoConsumido]++;
                            }

                            // Verificar las capacidades cruzadas
                            foreach (var kvp in conteoNuevosPorRefTipo)
                            {
                                string refTipo = kvp.Key;
                                int cantidadNuevos = kvp.Value;
                                var cap = cuarto.Caps[refTipo];

                                if (cap.Ocupados + cantidadNuevos > cap.Max)
                                    throw new Exception("Capacidad excedida. Alguien más acaba de ocupar el último lugar. Inténtalo de nuevo.");

                                // Actualizar memoria por si vienen más request a la misma habitación
                                cuarto.Caps[refTipo] = (cap.Max, cap.Ocupados + cantidadNuevos);
                            }
                        }

                        // 7. INSERCIÓN ATÓMICA LOTE COMPLETO
                        foreach (var req in reqs)
                        {
                            foreach (int asisId in req.IdsAsistentes)
                            {
                                await new NpgsqlCommand($"DELETE FROM \"Eventos_I_Alojamiento_Asignaciones\" WHERE \"Id_Asistente\" = {asisId}", conexion, trans).ExecuteNonQueryAsync();

                                using (var cmdI = new NpgsqlCommand(@"INSERT INTO ""Eventos_I_Alojamiento_Asignaciones"" (""Id_Alojamiento"", ""Id_Asistente"") VALUES (@a, @usr)", conexion, trans))
                                {
                                    cmdI.Parameters.AddWithValue("@a", req.IdAlojamiento);
                                    cmdI.Parameters.AddWithValue("@usr", asisId);
                                    await cmdI.ExecuteNonQueryAsync();
                                }
                            }
                        }

                        await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Crear, $"Auto-Asignó {reqs.Count} cuartos mediante portal a {todosAsistentesIds.Count} personas. Género: {generoGrupoFinal}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);
                        await trans.CommitAsync();

                        // NOTIFICACIÓN EN TIEMPO REAL A TODOS LOS NAVEGADORES (FUERA DE LA TRANSACCIÓN)
                        if (idEventoDetectado > 0)
                        {
                            // Disparamos para actualizar los mapas del admin (si lo tienes)
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("RefrescarMapa");
                            // Disparamos el evento exclusivo para que el portal se actualice fluido
                            await _eventosHub.Clients.Group($"Evento_{idEventoDetectado}").SendAsync("AlojamientoActualizado");
                        }

                        return Json(new { exito = true, mensaje = "¡Asignación exitosa!" });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        /// <summary>
        /// GET: Muestra la ventana con el catálogo de invitados de cortesía otorgados para un evento específico.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> InvitadosCortesia(string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de administrador para ver este catálogo.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);
            ViewBag.IdEvento = idEvento;
            ViewBag.IdEventoEncriptado = sid;
            ViewBag.TituloEvento = "";

            var listaInvitados = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Obtener título del evento para el encabezado
                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", conexion))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        ViewBag.TituloEvento = (string)await cmdEv.ExecuteScalarAsync();
                    }

                    // 2. Obtener el catálogo filtrando estrictamente por el nuevo flag booleano Es_Cortesia
                    string sql = @"
                        SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Correo_Externo"", b.""Telefono_Externo"", 
                               b.""Edad"", b.""Genero"", r.""Fecha_Registro"",
                               COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                               t.""Comentarios_Revision"" as ""Motivo""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                        LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON c.""Id_Asistente"" = b.""Id_Asistente""
                        LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON c.""Id_Cuenta"" = pa.""Id_Cuenta""
                        LEFT JOIN ""Eventos_D_Transacciones"" t ON pa.""Id_Transaccion"" = t.""Id_Transaccion""
                        WHERE r.""Id_Evento"" = @id AND b.""Es_Cortesia"" = TRUE
                        GROUP BY b.""Id_Asistente"", b.""Nombre_Completo"", b.""Correo_Externo"", b.""Telefono_Externo"", 
                                 b.""Edad"", b.""Genero"", r.""Fecha_Registro"", s.""Nombre"", t.""Comentarios_Revision""
                        ORDER BY r.""Fecha_Registro"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string motivoCompleto = r["Motivo"]?.ToString() ?? "";
                                string motivoLimpio = motivoCompleto.StartsWith("Cortesía VIP | ")
                                    ? motivoCompleto.Replace("Cortesía VIP | ", "")
                                    : motivoCompleto;

                                listaInvitados.Add(new
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Correo = r["Correo_Externo"]?.ToString() ?? "No registrado",
                                    Telefono = r["Telefono_Externo"]?.ToString() ?? "No registrado",
                                    Edad = (int)r["Edad"],
                                    Genero = r["Genero"].ToString() == "H" ? "Hombre" : "Mujer",
                                    Fecha = (DateTime)r["Fecha_Registro"],
                                    Modalidad = r["Modalidad"].ToString(),
                                    Motivo = string.IsNullOrWhiteSpace(motivoLimpio) ? "No especificado" : motivoLimpio
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cargar el catálogo: " + ex.Message, TipoMensaje.Error);
            }

            return View(listaInvitados);
        }

        /// <summary>
        /// AJAX: Obtiene las modalidades y su disponibilidad en tiempo real para el modal de Cortesías.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ApiGetModalidadesCortesia(int idEvento)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin)) return Json(new { exito = false, mensaje = "Sin permisos" });
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    var disp = await ConsultarDisponibilidadEvento(idEvento, con);
                    if (disp == null) return Json(new { exito = false, mensaje = "Evento no encontrado" });

                    var opciones = disp.Modalidades.Select(m => new {
                        id = m.IdSubtipo,
                        nombre = m.NombreModalidad,
                        disponibles = m.LugaresDisponibles
                    }).ToList();

                    return Json(new
                    {
                        exito = true,
                        requiereModalidad = disp.Modalidades.Any(),
                        modalidades = opciones,
                        disponiblesGlobal = disp.DisponiblesGlobal
                    });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        /// <summary>
        /// POST: Registra un asistente especial saltándose la pasarela de pago y registrando $0.00 de ingreso.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarInvitadoCortesia(int Id_Evento, string NombreCompleto, int Edad, string Genero, int? Id_Subtipo_Seleccionado, string Correo, string Telefono, string MotivoCortesia, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Error", "Solo los administrators pueden otorgar cortesías.", TipoMensaje.Alerta);
                return RedirectToAction("PanelControl", new { sid = sid });
            }

            int idUserAdmin = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (await ValidarEventoBloqueado(Id_Evento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "No se pueden agregar cortesías porque el evento está bloqueado.", TipoMensaje.Error);
                        return RedirectToAction("InvitadosCortesia", new { sid = sid });
                    }
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. Validar Disponibilidad y Bloquear Fila (FOR UPDATE) para evitar sobreventa
                        var disp = await ConsultarDisponibilidadEvento(Id_Evento, conexion, trans, forUpdate: true);

                        if (disp == null || !disp.EventoActivo)
                            throw new Exception("El evento no existe o ya está cerrado.");

                        decimal costoAAsignar = disp.CostoEntrada;

                        if (disp.Modalidades != null && disp.Modalidades.Any())
                        {
                            if (!Id_Subtipo_Seleccionado.HasValue)
                                throw new Exception("Debes seleccionar una modalidad obligatoriamente.");

                            var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == Id_Subtipo_Seleccionado.Value);
                            if (modal == null) throw new Exception("La modalidad seleccionada es inválida.");

                            if (modal.LugaresDisponibles <= 0)
                                throw new Exception($"Operación rechazada: La modalidad '{modal.NombreModalidad}' está completamente agotada.");

                            costoAAsignar = modal.Costo;
                        }
                        else
                        {
                            if (disp.DisponiblesGlobal <= 0)
                                throw new Exception("Operación rechazada: El cupo general del evento está completamente agotado.");
                        }

                        // 2. Crear Registro Padre (A)
                        int idRegistro = 0;
                        using (var cmdR = new NpgsqlCommand(@"INSERT INTO ""Eventos_A_Registros"" (""Id_Evento"", ""Id_Usuario"", ""Fecha_Registro"") VALUES (@ev, @usr, NOW()) RETURNING ""Id_Registro""", conexion, trans))
                        {
                            cmdR.Parameters.AddWithValue("@ev", Id_Evento);
                            cmdR.Parameters.AddWithValue("@usr", idUserAdmin);
                            idRegistro = (int)await cmdR.ExecuteScalarAsync();
                        }

                        // 3. Crear Asistente (B) - AGREGAMOS LA COLUMNA Es_Cortesia CON VALOR TRUE EN EL INSERT
                        string token = GenerarTokenAmigable(NombreCompleto);
                        int idAsistente = 0;
                        string sqlAsis = @"INSERT INTO ""Eventos_B_Asistentes"" 
                          (""Id_Registro"", ""Nombre_Completo"", ""Etiqueta_Grupo"", ""Token_Pago_Externo"", ""Edad"", ""Genero"", ""Id_Subtipo"", ""Es_Pagado"", ""Correo_Externo"", ""Telefono_Externo"", ""Es_Cortesia"") 
                          VALUES (@reg, @nom, 'Cortesía VIP', @tok, @edad, @gen, @sub, TRUE, @email, @tel, TRUE) 
                          RETURNING ""Id_Asistente""";

                        using (var cmdA = new NpgsqlCommand(sqlAsis, conexion, trans))
                        {
                            cmdA.Parameters.AddWithValue("@reg", idRegistro);
                            cmdA.Parameters.AddWithValue("@nom", NombreCompleto.Trim());
                            cmdA.Parameters.AddWithValue("@tok", token);
                            cmdA.Parameters.AddWithValue("@edad", Edad);
                            cmdA.Parameters.AddWithValue("@gen", Genero);
                            cmdA.Parameters.AddWithValue("@sub", (object)Id_Subtipo_Seleccionado ?? DBNull.Value);
                            cmdA.Parameters.AddWithValue("@email", string.IsNullOrWhiteSpace(Correo) ? DBNull.Value : (object)Correo.Trim());
                            cmdA.Parameters.AddWithValue("@tel", string.IsNullOrWhiteSpace(Telefono) ? DBNull.Value : (object)Telefono.Trim());
                            idAsistente = (int)await cmdA.ExecuteScalarAsync();
                        }

                        // 4. Crear Deudas y marcarlas como Pagadas (C)
                        var idsCuentas = new List<int>();
                        string sqlDeuda = @"INSERT INTO ""Eventos_C_Cuentas_Cobrar"" (""Id_Asistente"", ""Id_Calendario"", ""Monto_Pagar"", ""Pagado"", ""Fecha_Pagado"")
                                            SELECT @idA, ""Id_Calendario"", 
                                            CASE WHEN (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) > 0 THEN
                                                ROUND(@costo * ( CAST(""Monto_Base"" AS DECIMAL) / (SELECT SUM(""Monto_Base"") FROM ""Eventos_Calendario"" WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))) ), 2)
                                            ELSE @costo END, TRUE, NOW()
                                            FROM ""Eventos_Calendario"" 
                                            WHERE ""Id_Evento""=@ev AND ""Numero_Pago"" > 0 AND ((""Id_Subtipo"" IS NULL AND @sub IS NULL) OR (""Id_Subtipo""=@sub))
                                            ORDER BY ""Numero_Pago"" ASC RETURNING ""Id_Cuenta""";

                        using (var cmdD = new NpgsqlCommand(sqlDeuda, conexion, trans))
                        {
                            cmdD.Parameters.AddWithValue("@idA", idAsistente);
                            cmdD.Parameters.AddWithValue("@ev", Id_Evento);
                            cmdD.Parameters.AddWithValue("@costo", costoAAsignar);
                            cmdD.Parameters.Add(new NpgsqlParameter("@sub", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)Id_Subtipo_Seleccionado ?? DBNull.Value });

                            using (var rCuentas = await cmdD.ExecuteReaderAsync())
                            {
                                while (await rCuentas.ReadAsync()) idsCuentas.Add((int)rCuentas[0]);
                            }

                            if (!idsCuentas.Any())
                            {
                                using (var cmdFallback = new NpgsqlCommand($"INSERT INTO \"Eventos_C_Cuentas_Cobrar\" (\"Id_Asistente\", \"Monto_Pagar\",\"Id_Calendario\", \"Pagado\", \"Fecha_Pagado\") VALUES ({idAsistente}, {costoAAsignar}, NULL, TRUE, NOW()) RETURNING \"Id_Cuenta\"", conexion, trans))
                                {
                                    idsCuentas.Add((int)await cmdFallback.ExecuteScalarAsync());
                                }
                            }
                        }

                        // 5. Crear la Transacción $0.00 (D)
                        string refUnica = $"VIP_{idUserAdmin}_{Guid.NewGuid().ToString("N").Substring(0, 5).ToUpper()}";
                        string sqlTrx = @"INSERT INTO ""Eventos_D_Transacciones"" 
                                    (""Id_Usuario"", ""Id_Evento"", ""Monto_Total"", ""Total_Neto"", ""Estatus_Pago"", ""Fecha_Intento"", ""Fecha_Pago_Aceptado"", ""Ref_Pasarela"", ""External_Reference"", ""Completado"", ""Comentarios_Revision"")
                                    VALUES (@usr, @ev, 0, 0, 'paid', NOW(), NOW(), @ref, @refExt, TRUE, @motivo) 
                                    RETURNING ""Id_Transaccion""";

                        int idTransaccion = 0;
                        using (var cmdT = new NpgsqlCommand(sqlTrx, conexion, trans))
                        {
                            cmdT.Parameters.AddWithValue("@usr", idUserAdmin);
                            cmdT.Parameters.AddWithValue("@ev", Id_Evento);
                            cmdT.Parameters.AddWithValue("@ref", refUnica);
                            cmdT.Parameters.AddWithValue("@refExt", refUnica);
                            cmdT.Parameters.AddWithValue("@motivo", "Cortesía VIP | " + MotivoCortesia);
                            idTransaccion = (int)await cmdT.ExecuteScalarAsync();
                        }

                        // 6. Aplicar los pagos (E)
                        string sqlE = @"INSERT INTO ""Eventos_E_Pagos_Aplicados"" (""Id_Transaccion"", ""Id_Cuenta"") VALUES (@tr, @cta)";
                        foreach (var idCta in idsCuentas)
                        {
                            using (var cmdE = new NpgsqlCommand(sqlE, conexion, trans))
                            {
                                cmdE.Parameters.AddWithValue("@tr", idTransaccion);
                                cmdE.Parameters.AddWithValue("@cta", idCta);
                                await cmdE.ExecuteNonQueryAsync();
                            }
                        }

                        // 7. Bitácora de Auditoría
                        await Funciones.RegistrarBitacora(conexion, idUserAdmin, Modulo, Parametros.AccionesBitacora.Crear, $"Otorgó Cortesía VIP a {NombreCompleto}. Motivo: {MotivoCortesia}", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                        await trans.CommitAsync();
                        MostrarMensaje("Cortesía Registrada", $"El invitado {NombreCompleto} fue registrado exitosamente con ingreso $0.00.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("InvitadosCortesia", new { sid = sid });
        }

        /// <summary>
        /// POST: Elimina de forma segura una cortesía VIP liberando el cupo, 
        /// SIEMPRE Y CUANDO todas sus deudas y transacciones sean estrictamente de $0.00 pesos.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevocarCortesia(int idAsistente, string sid)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Acceso Denegado", "Solo los administradores supremos pueden revocar cortesías.", TipoMensaje.Alerta);
                return RedirectToAction("InvitadosCortesia", new { sid = sid });
            }

            int idUserAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            int idEvento = Funciones.DesencriptarId(sid);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    if (await ValidarEventoBloqueado(idEvento, conexion))
                    {
                        MostrarMensaje("Evento Bloqueado", "El evento está bloqueado. No se pueden revocar cortesías.", TipoMensaje.Error);
                        return RedirectToAction("InvitadosCortesia", new { sid = sid });
                    }
                    // =========================================================================================
                    // BLINDAJE DE SEGURIDAD EXTREMA (CORREGIDO): 
                    // Detecta si existe al menos una Transacción real con un valor monetario mayor a cero.
                    // Ignoramos la tabla de Cuentas porque las cortesías conservan el valor teórico del lugar.
                    // =========================================================================================
                    string sqlCheckCostos = @"
                        SELECT 
                            COUNT(DISTINCT CASE WHEN t.""Id_Transaccion"" IS NOT NULL AND t.""Monto_Total"" > 0 THEN t.""Id_Transaccion"" END) as ""TrxConValor"",
                            MAX(b.""Nombre_Completo"") as ""Nombre"",
                            MAX(r.""Id_Registro"") as ""IdRegistro""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON b.""Id_Asistente"" = c.""Id_Asistente""
                        LEFT JOIN ""Eventos_E_Pagos_Aplicados"" pa ON c.""Id_Cuenta"" = pa.""Id_Cuenta""
                        LEFT JOIN ""Eventos_D_Transacciones"" t ON pa.""Id_Transaccion"" = t.""Id_Transaccion""
                        WHERE b.""Id_Asistente"" = @idA AND r.""Id_Evento"" = @idEv
                        GROUP BY b.""Id_Asistente""";

                    long trxConValor = 0;
                    string nombreAsistente = "";
                    int idRegistroPadre = 0;

                    using (var cmdCheck = new NpgsqlCommand(sqlCheckCostos, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idA", idAsistente);
                        cmdCheck.Parameters.AddWithValue("@idEv", idEvento);
                        using (var r = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                trxConValor = Convert.ToInt64(r["TrxConValor"]);
                                nombreAsistente = r["Nombre"].ToString();
                                idRegistroPadre = Convert.ToInt32(r["IdRegistro"]);
                            }
                            else
                            {
                                throw new Exception("El invitado especificado no existe o no pertenece a este evento.");
                            }
                        }
                    }

                    // REGLA DE ORO: Bloqueo inmediato SOLO si se detecta una transacción con dinero real.
                    if (trxConValor > 0)
                    {
                        MostrarMensaje("Operación Cancelada por Seguridad",
                            $"No se puede revocar la cortesía de '{nombreAsistente}' porque el sistema detectó transacciones financieras reales asociadas a este registro que son mayores a cero ($0.00).",
                            TipoMensaje.Error);

                        return RedirectToAction("InvitadosCortesia", new { sid = sid });
                    }

                    // =========================================================================================
                    // PROCESO QUIRÚRGICO DE ELIMINACIÓN (ATÓMICO)
                    // Como el check anterior pasó, tenemos la certeza absoluta de que todo vale $0.00
                    // =========================================================================================
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. Recolectar las transacciones de cortesía vinculadas para borrarlas de la tabla D
                            var idsTrxABorrar = new List<int>();
                            string sqlGetTrx = @"
                                SELECT DISTINCT pa.""Id_Transaccion"" 
                                FROM ""Eventos_C_Cuentas_Cobrar"" c
                                JOIN ""Eventos_E_Pagos_Aplicados"" pa ON c.""Id_Cuenta"" = pa.""Id_Cuenta""
                                WHERE c.""Id_Asistente"" = @idA";

                            using (var cmdGetT = new NpgsqlCommand(sqlGetTrx, conexion, trans))
                            {
                                cmdGetT.Parameters.AddWithValue("@idA", idAsistente);
                                using (var rT = await cmdGetT.ExecuteReaderAsync())
                                {
                                    while (await rT.ReadAsync()) idsTrxABorrar.Add((int)rT[0]);
                                }
                            }

                            if (idsTrxABorrar.Any())
                            {
                                // B. Borrar enlaces en la tabla puente (E)
                                string sqlDelE = @"DELETE FROM ""Eventos_E_Pagos_Aplicados"" WHERE ""Id_Transaccion"" = ANY(@ids)";
                                using (var cmdDelE = new NpgsqlCommand(sqlDelE, conexion, trans))
                                {
                                    cmdDelE.Parameters.AddWithValue("@ids", idsTrxABorrar);
                                    await cmdDelE.ExecuteNonQueryAsync();
                                }

                                // C. Borrar cabeceras de transacciones de cero pesos (D)
                                string sqlDelD = @"DELETE FROM ""Eventos_D_Transacciones"" WHERE ""Id_Transaccion"" = ANY(@ids)";
                                using (var cmdDelD = new NpgsqlCommand(sqlDelD, conexion, trans))
                                {
                                    cmdDelD.Parameters.AddWithValue("@ids", idsTrxABorrar);
                                    await cmdDelD.ExecuteNonQueryAsync();
                                }
                            }

                            // D. Borrar cuentas por cobrar saldadas de cero pesos (C)
                            string sqlDelC = @"DELETE FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = @idA";
                            using (var cmdDelC = new NpgsqlCommand(sqlDelC, conexion, trans))
                            {
                                cmdDelC.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelC.ExecuteNonQueryAsync();
                            }

                            // E. Retirar asignaciones de camas/hospedaje si ya se había acomodado
                            await new NpgsqlCommand($@"DELETE FROM ""Eventos_I_Alojamiento_Asignaciones"" WHERE ""Id_Asistente"" = {idAsistente}", conexion, trans).ExecuteNonQueryAsync();

                            // F. Borrar de forma definitiva al Asistente (B)
                            string sqlDelPE = @"DELETE FROM ""Eventos_Asistentes_ProductosExtra"" WHERE ""Id_Asistente"" = @idA";
                            using (var cmdDelPE = new NpgsqlCommand(sqlDelPE, conexion, trans))
                            {
                                cmdDelPE.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelPE.ExecuteNonQueryAsync();
                            }

                            string sqlDelB = @"DELETE FROM ""Eventos_B_Asistentes"" WHERE ""Id_Asistente"" = @idA";
                            using (var cmdDelB = new NpgsqlCommand(sqlDelB, conexion, trans))
                            {
                                cmdDelB.Parameters.AddWithValue("@idA", idAsistente);
                                await cmdDelB.ExecuteNonQueryAsync();
                            }

                            // G. Limpiar el Registro Padre (A) para no dejar basura si se quedó sin miembros
                            long restantes = (long)await new NpgsqlCommand($@"SELECT COUNT(*) FROM ""Eventos_B_Asistentes"" WHERE ""Id_Registro"" = {idRegistroPadre}", conexion, trans).ExecuteScalarAsync();
                            if (restantes == 0)
                            {
                                await new NpgsqlCommand($@"DELETE FROM ""Eventos_A_Registros"" WHERE ""Id_Registro"" = {idRegistroPadre}", conexion, trans).ExecuteNonQueryAsync();
                            }

                            // H. Registrar el movimiento en la Bitácora de Auditoría
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUserAdmin, Modulo, Parametros.AccionesBitacora.Borrar, $"Revocó y eliminó la Cortesía VIP de {nombreAsistente}. Cupo liberado.", ip, trans);

                            await trans.CommitAsync();

                            // Notificaciones reactivas en segundo plano (SignalR) para limpiar las pantallas de los demás admins en vivo
                            await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("AlojamientoActualizado");
                            await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("RefrescarMapa");
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                MostrarMensaje("Cortesía Revocada", "El registro fue eliminado por completo de las listas y el cupo se encuentra disponible nuevamente.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo revocar la cortesía: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("InvitadosCortesia", new { sid = sid });
        }

        /// <summary>
        /// GET: Muestra la pantalla para seleccionar a los asistentes y cobrarles en efectivo.
        /// Solo lista a los que tienen deuda pendiente. Requiere admin cruzado en Eventos y Caja.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> CobroEfectivo(string sid)
        {
            // COMPROBACIÓN CRUZADA OBLIGATORIA EN BACKEND
            bool tienePermisoEventos = User.TienePermiso(Parametros.Modulos.Eventos, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Eventos, PermisoEditar);
            bool tienePermisoCaja = User.TienePermiso(Parametros.Modulos.Caja, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Caja, PermisoEditar);

            if (!tienePermisoEventos || !tienePermisoCaja)
            {
                MostrarMensaje("Acceso Denegado", "No cuentas con autorizaciones suficientes en los módulos de Eventos y Caja Chica simultáneamente para efectuar cobros en ventanilla.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idEvento = Funciones.DesencriptarId(sid);
            ViewBag.IdEventoEncriptado = sid;
            var listaMorosos = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
                        SELECT 
                            b.""Id_Asistente"", b.""Nombre_Completo"",
                            COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                            u.""NombreCompleto"" as ""NombreInscriptor"",
                            (SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE) as ""DeudaRestante"",
                            (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c2 WHERE c2.""Id_Asistente"" = b.""Id_Asistente"" AND c2.""Pagado"" = TRUE) as ""PagosPrevios""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                        WHERE r.""Id_Evento"" = @id
                        AND EXISTS (SELECT 1 FROM ""Eventos_C_Cuentas_Cobrar"" cx WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND cx.""Pagado"" = FALSE)
                        ORDER BY b.""Nombre_Completo"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                listaMorosos.Add(new
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    Nombre = r["Nombre_Completo"].ToString(),
                                    Modalidad = r["Modalidad"].ToString(),
                                    NombreInscriptor = r["NombreInscriptor"].ToString(),
                                    DeudaRestante = (decimal)r["DeudaRestante"],
                                    PagosPrevios = Convert.ToInt32(r["PagosPrevios"])
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("PanelControl", new { sid = sid });
            }

            return View(listaMorosos);
        }

        /// <summary>
        /// POST: Procesa el cobro en efectivo.
        /// Neutraliza la deuda a $0, respalda el monto en la nueva tabla Log, marca como pagado e inyecta un solo ingreso a la Caja General.
        /// Requiere admin cruzado en Eventos y Caja.
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarCobroEfectivo(string sid, List<int> idsAsistentes)
        {
            // COMPROBACIÓN CRUZADA OBLIGATORIA EN BACKEND
            bool tienePermisoEventos = User.TienePermiso(Parametros.Modulos.Eventos, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Eventos, PermisoEditar);
            bool tienePermisoCaja = User.TienePermiso(Parametros.Modulos.Caja, PermisoAdmin) || User.TienePermiso(Parametros.Modulos.Caja, PermisoEditar);

            if (!tienePermisoEventos || !tienePermisoCaja)
            {
                MostrarMensaje("Acceso Denegado", "No cuentas con autorizaciones suficientes en los módulos de Eventos y Caja Chica simultáneamente para efectuar cobros en ventanilla.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            if (idsAsistentes == null || !idsAsistentes.Any())
            {
                MostrarMensaje("Atención", "No seleccionaste a ningún asistente para cobrar.", TipoMensaje.Alerta);
                return RedirectToAction("CobroEfectivo", new { sid = sid });
            }

            int idEvento = Funciones.DesencriptarId(sid);
            int idCajero = int.Parse(User.FindFirst("IdUsuario").Value);
            decimal montoTotalCobrado = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. OBTENER INFORMACIÓN DE LOS ASISTENTES SELECCIONADOS (PARA VALIDAR CUPO)
                    var asistentesInfo = new List<dynamic>();
                    string sqlInfoAsistentes = @"
                        SELECT b.""Id_Asistente"", b.""Id_Subtipo"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" cx WHERE cx.""Id_Asistente"" = b.""Id_Asistente"" AND cx.""Pagado"" = TRUE) as ""PagosHechos""
                        FROM ""Eventos_B_Asistentes"" b
                        WHERE b.""Id_Asistente"" = ANY(@ids)";

                    using (var cmdInfo = new NpgsqlCommand(sqlInfoAsistentes, conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@ids", idsAsistentes);
                        using (var r = await cmdInfo.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                asistentesInfo.Add(new
                                {
                                    IdAsistente = (int)r["Id_Asistente"],
                                    IdSubtipo = r["Id_Subtipo"] != DBNull.Value ? (int?)r["Id_Subtipo"] : null,
                                    TieneLugarAsegurado = Convert.ToInt32(r["PagosHechos"]) > 0
                                });
                            }
                        }
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 2. VALIDAR CUPO (ESTRICTO) PARA LOS QUE NO TENÍAN LUGAR ASEGURADO
                            var asistentesNuevos = asistentesInfo.Where(x => !x.TieneLugarAsegurado).ToList();

                            if (asistentesNuevos.Any())
                            {
                                // Extraemos los IDs de los que son totalmente nuevos para excluirlos en la función (porque actualmente no están ocupando lugar real)
                                var idsExcluir = asistentesNuevos.Select(x => (int)x.IdAsistente).ToList();
                                var disp = await ConsultarDisponibilidadEvento(idEvento, conexion, trans, true, idsExcluir);

                                if (disp == null || !disp.EventoActivo)
                                    throw new Exception("El evento no existe o ya está cerrado.");

                                if (disp.Modalidades != null && disp.Modalidades.Any())
                                {
                                    // Agrupamos cuántos lugares nuevos por modalidad se están cobrando
                                    var conteoNuevosPorSubtipo = asistentesNuevos
                                        .Where(x => x.IdSubtipo != null)
                                        .GroupBy(x => (int)x.IdSubtipo)
                                        .ToDictionary(g => g.Key, g => g.Count());

                                    foreach (var kvp in conteoNuevosPorSubtipo)
                                    {
                                        var modal = disp.Modalidades.FirstOrDefault(m => m.IdSubtipo == kvp.Key);
                                        if (modal == null) throw new Exception("Una modalidad seleccionada es inválida.");

                                        if (modal.LugaresDisponibles < kvp.Value)
                                            throw new Exception($"Cupo Excedido: Solo quedan {modal.LugaresDisponibles} lugares en la modalidad '{modal.NombreModalidad}' y estás intentando cobrar a {kvp.Value} personas sin lugar asegurado.");
                                    }
                                }
                                else
                                {
                                    if (disp.DisponiblesGlobal < asistentesNuevos.Count)
                                        throw new Exception($"Cupo Global Excedido: Quedan {disp.DisponiblesGlobal} lugares disponibles y quieres cobrar a {asistentesNuevos.Count} personas.");
                                }
                            }

                            // 3. PROCESAR CADA CUENTA (NEUTRALIZACIÓN Y RESPALDO)
                            string sqlCuentas = @"SELECT ""Id_Cuenta"", ""Monto_Pagar"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Asistente"" = ANY(@ids) AND ""Pagado"" = FALSE";

                            var cuentasProcesar = new List<dynamic>();
                            using (var cmdCuentas = new NpgsqlCommand(sqlCuentas, conexion, trans))
                            {
                                cmdCuentas.Parameters.AddWithValue("@ids", idsAsistentes);
                                using (var r = await cmdCuentas.ExecuteReaderAsync())
                                {
                                    while (await r.ReadAsync())
                                    {
                                        cuentasProcesar.Add(new { IdCuenta = (int)r["Id_Cuenta"], Monto = (decimal)r["Monto_Pagar"] });
                                    }
                                }
                            }

                            if (!cuentasProcesar.Any())
                                throw new Exception("Los asistentes seleccionados ya no tienen deudas pendientes. Posiblemente alguien más les cobró.");

                            foreach (var cuenta in cuentasProcesar)
                            {
                                montoTotalCobrado += cuenta.Monto;

                                // A. Respaldar en la nueva tabla (Log Histórico)
                                string sqlLog = @"INSERT INTO ""Eventos_Ingresos_Efectivo_Log"" (""Id_Cuenta"", ""Monto_Original"", ""Id_Usuario_Cajero"", ""Fecha_Cobro"") VALUES (@cta, @monto, @usr, NOW())";
                                using (var cmdLog = new NpgsqlCommand(sqlLog, conexion, trans))
                                {
                                    cmdLog.Parameters.AddWithValue("@cta", cuenta.IdCuenta);
                                    cmdLog.Parameters.AddWithValue("@monto", cuenta.Monto);
                                    cmdLog.Parameters.AddWithValue("@usr", idCajero);
                                    await cmdLog.ExecuteNonQueryAsync();
                                }

                                // B. Neutralizar y Marcar Pagado (Tu genial idea para no alterar la BD principal)
                                string sqlUpd = @"UPDATE ""Eventos_C_Cuentas_Cobrar"" SET ""Monto_Pagar"" = 0, ""Pagado"" = TRUE, ""Fecha_Pagado"" = NOW() WHERE ""Id_Cuenta"" = @cta";
                                using (var cmdUpd = new NpgsqlCommand(sqlUpd, conexion, trans))
                                {
                                    cmdUpd.Parameters.AddWithValue("@cta", cuenta.IdCuenta);
                                    await cmdUpd.ExecuteNonQueryAsync();
                                }
                            }

                            // 4. ASEGURAR QUE LOS ASISTENTES CAMBIEN A "Es_Pagado" = TRUE
                            string sqlUpdAsis = @"UPDATE ""Eventos_B_Asistentes"" SET ""Es_Pagado"" = TRUE WHERE ""Id_Asistente"" = ANY(@ids)";
                            using (var cmdUpdAsis = new NpgsqlCommand(sqlUpdAsis, conexion, trans))
                            {
                                cmdUpdAsis.Parameters.AddWithValue("@ids", idsAsistentes);
                                await cmdUpdAsis.ExecuteNonQueryAsync();
                            }

                            // 5. INYECTAR EL DINERO REAL DIRECTO A LA CAJA GENERAL
                            string sqlCaja = @"INSERT INTO ""Fin_Caja"" (""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Id_Evento"") 
                                               VALUES (@concepto, @monto, 'Ingreso', NOW(), @usr, FALSE, @idEv)";
                            using (var cmdCaja = new NpgsqlCommand(sqlCaja, conexion, trans))
                            {
                                cmdCaja.Parameters.AddWithValue("@concepto", $"Cobro en efectivo Evento - Liquidación de {idsAsistentes.Count} asistentes");
                                cmdCaja.Parameters.AddWithValue("@monto", montoTotalCobrado);
                                cmdCaja.Parameters.AddWithValue("@usr", idCajero);
                                cmdCaja.Parameters.AddWithValue("@idEv", idEvento);
                                await cmdCaja.ExecuteNonQueryAsync();
                            }

                            // 6. BITÁCORA
                            await Funciones.RegistrarBitacora(conexion, idCajero, Modulo, Parametros.AccionesBitacora.Crear, $"Recibió en Efectivo ${montoTotalCobrado:N2} liquidando la deuda de {idsAsistentes.Count} asistente(s) del Evento #{idEvento}.", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1", trans);

                            await trans.CommitAsync();

                            // Notificar a clientes conectados que hay lugares ocupados nuevos
                            await _eventosHub.Clients.Group($"Evento_{idEvento}").SendAsync("RefrescarMapa");

                            MostrarMensaje("Cobro Exitoso", $"Has recibido {montoTotalCobrado:C2} en efectivo y se ingresó correctamente a la caja general. Las cuentas han sido liquidadas.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Crítico", ex.Message, TipoMensaje.Error);
                return RedirectToAction("CobroEfectivo", new { sid = sid });
            }

            return RedirectToAction("PanelControl", new { sid = sid });
        }

        /// <summary>
        /// HELPER: Obtiene la lista estructurada del avance de pagos (Ritmo de Inscripciones).
        /// Utilizada tanto por la Vista Web como por la exportación a Excel.
        /// </summary>
        private async Task<List<ItemAvancePago>> ObtenerDatosRitmoInscripciones(int idEvento, int? idSubtipo, DateTime fechaCorte, NpgsqlConnection con)
        {
            var resultados = new List<ItemAvancePago>();

            string sqlAvance = @"
                SELECT 
                    b.""Id_Asistente"", b.""Nombre_Completo"", u.""NombreCompleto"" AS ""Inscriptor"",
                    COALESCE(s.""Nombre"", 'Entrada General') AS ""Modalidad"",
                    COALESCE(s.""Costo"", e.""Costo_Entrada"") AS ""Costo_Oficial"",
                    COALESCE(SUM(
                        CASE 
                            WHEN log.""Id_Log"" IS NOT NULL AND log.""Fecha_Cobro"" <= @corte THEN log.""Monto_Original""
                            WHEN log.""Id_Log"" IS NULL AND c.""Fecha_Pagado"" <= @corte THEN c.""Monto_Pagar""
                            ELSE 0 
                        END
                    ), 0) AS ""Monto_Pagado_Al_Corte""
                FROM ""Eventos_B_Asistentes"" b
                JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario""
                LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                LEFT JOIN ""Eventos_C_Cuentas_Cobrar"" c ON b.""Id_Asistente"" = c.""Id_Asistente"" AND c.""Pagado"" = TRUE
                LEFT JOIN ""Eventos_Ingresos_Efectivo_Log"" log ON c.""Id_Cuenta"" = log.""Id_Cuenta""
                WHERE r.""Id_Evento"" = @idEvento 
                  AND (@idSub IS NULL OR @idSub = 0 OR b.""Id_Subtipo"" = @idSub)
                  -- Hemos eliminado el filtro de r.""Fecha_Registro"" <= @corte para que aparezca el padrón completo
                GROUP BY b.""Id_Asistente"", b.""Nombre_Completo"", u.""NombreCompleto"", s.""Nombre"", s.""Costo"", e.""Costo_Entrada""
                ORDER BY ""Monto_Pagado_Al_Corte"" DESC, b.""Nombre_Completo"" ASC";

            using (var cmd = new NpgsqlCommand(sqlAvance, con))
            {
                cmd.Parameters.AddWithValue("@idEvento", idEvento);
                cmd.Parameters.Add(new NpgsqlParameter("@idSub", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object)idSubtipo ?? DBNull.Value });
                cmd.Parameters.AddWithValue("@corte", fechaCorte);

                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        resultados.Add(new ItemAvancePago
                        {
                            IdAsistente = (int)r["Id_Asistente"],
                            Asistente = r["Nombre_Completo"].ToString(),
                            Inscriptor = r["Inscriptor"].ToString(),
                            Modalidad = r["Modalidad"].ToString(),
                            CostoOficial = (decimal)r["Costo_Oficial"],
                            MontoPagadoAlCorte = (decimal)r["Monto_Pagado_Al_Corte"]
                        });
                    }
                }
            }
            return resultados;
        }

        /// <summary>
        /// HELPER: Obtiene el desglose histórico de un asistente sin producto cartesiano.
        /// Utilizada por el Modal AJAX y por la exportación a Excel individual.
        /// </summary>
        private async Task<List<DetallePagoModalItem>> ObtenerHistorialPagosAsistente(int idAsistente, NpgsqlConnection con)
        {
            var pagos = new List<DetallePagoModalItem>();

            string sqlPagos = @"
                SELECT 
                    cal.""Nombre_Concepto"", cal.""Numero_Pago"",
                    CASE WHEN log.""Id_Log"" IS NOT NULL THEN log.""Monto_Original"" ELSE c.""Monto_Pagar"" END AS ""Monto_Pagado"",
                    CASE WHEN log.""Id_Log"" IS NOT NULL THEN log.""Fecha_Cobro"" ELSE c.""Fecha_Pagado"" END AS ""Fecha_Pago"",
                    CASE 
                        WHEN log.""Id_Log"" IS NOT NULL THEN 'Efectivo / Caja'
                        WHEN trx.""EsTransferencia"" = TRUE THEN 'Transferencia'
                        ELSE 'Pasarela / Tarjeta'
                    END AS ""Metodo"",
                    CASE 
                        WHEN log.""Id_Log"" IS NOT NULL THEN 'CAJA-' || log.""Id_Log""
                        ELSE COALESCE(trx.""Ref_Pasarela"", trx.""External_Reference"")
                    END AS ""Referencia""
                FROM ""Eventos_C_Cuentas_Cobrar"" c
                JOIN ""Eventos_Calendario"" cal ON c.""Id_Calendario"" = cal.""Id_Calendario""
                LEFT JOIN ""Eventos_Ingresos_Efectivo_Log"" log ON c.""Id_Cuenta"" = log.""Id_Cuenta""
                LEFT JOIN LATERAL (
                    SELECT t.""EsTransferencia"", t.""Ref_Pasarela"", t.""External_Reference""
                    FROM ""Eventos_E_Pagos_Aplicados"" pa
                    JOIN ""Eventos_D_Transacciones"" t ON pa.""Id_Transaccion"" = t.""Id_Transaccion""
                    WHERE pa.""Id_Cuenta"" = c.""Id_Cuenta"" 
                      AND t.""Completado"" = TRUE 
                      AND t.""Estatus_Pago"" IN ('paid', 'approved')
                    LIMIT 1
                ) trx ON true
                WHERE c.""Id_Asistente"" = @idA AND c.""Pagado"" = TRUE
                ORDER BY ""Fecha_Pago"" ASC, cal.""Numero_Pago"" ASC";

            using (var cmd = new NpgsqlCommand(sqlPagos, con))
            {
                cmd.Parameters.AddWithValue("@idA", idAsistente);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        string concepto = r["Nombre_Concepto"].ToString();
                        pagos.Add(new DetallePagoModalItem
                        {
                            Concepto = string.IsNullOrEmpty(concepto) ? $"Plazo #{r["Numero_Pago"]}" : concepto,
                            MontoPagado = (decimal)r["Monto_Pagado"],
                            FechaPago = ((DateTime)r["Fecha_Pago"]).ToString("dd/MM/yyyy HH:mm"),
                            Metodo = r["Metodo"].ToString(),
                            Referencia = r["Referencia"]?.ToString() ?? "N/D"
                        });
                    }
                }
            }
            return pagos;
        }


        [Authorize]
        [HttpGet("Eventos/RitmoInscripciones/{sid}")]
        public async Task<IActionResult> RitmoInscripciones(string sid, int? idSubtipo, DateTime? fechaCorte, decimal porcentajeMeta = 50)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Forbid();

            int idEvento = Funciones.DesencriptarId(sid);
            DateTime fechaFiltrar = fechaCorte.HasValue ? fechaCorte.Value.Date.AddDays(1).AddTicks(-1) : DateTime.Now.Date.AddDays(1).AddTicks(-1);

            var modelo = new RitmoInscripcionesViewModel
            {
                IdEventoEncriptado = sid,
                IdEvento = idEvento,
                FechaCorte = fechaFiltrar.Date,
                PorcentajeMeta = porcentajeMeta,
                IdSubtipoFiltro = idSubtipo
            };

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        modelo.TituloEvento = (string)await cmdEv.ExecuteScalarAsync();
                    }

                    string sqlSub = @"SELECT ""Id_Subtipo"", ""Nombre"" FROM ""Eventos_Subtipos"" WHERE ""Id_Evento"" = @id AND ""Activo"" = TRUE";
                    using (var cmdSub = new NpgsqlCommand(sqlSub, con))
                    {
                        cmdSub.Parameters.AddWithValue("@id", idEvento);
                        using (var r = await cmdSub.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                                modelo.SubtiposDisponibles.Add(new SubtipoEventoItem { Id_Subtipo = (int)r["Id_Subtipo"], Nombre = r["Nombre"].ToString() });
                        }
                    }

                    modelo.Resultados = await ObtenerDatosRitmoInscripciones(idEvento, idSubtipo, fechaFiltrar, con);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DescargarRitmoInscripcionesExcel(string sid, int? idSubtipo, DateTime? fechaCorte, decimal porcentajeMeta = 50)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Forbid();

            int idEvento = Funciones.DesencriptarId(sid);
            DateTime fechaFiltrar = fechaCorte.HasValue ? fechaCorte.Value.Date.AddDays(1).AddTicks(-1) : DateTime.Now.Date.AddDays(1).AddTicks(-1);
            string tituloEvento = "Ritmo_Inscripciones";

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    using (var cmdEv = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id", con))
                    {
                        cmdEv.Parameters.AddWithValue("@id", idEvento);
                        var resEv = await cmdEv.ExecuteScalarAsync();
                        if (resEv != null) tituloEvento = resEv.ToString().Replace(" ", "_");
                    }

                    // 👉 LLAMADA AL HELPER (La misma fuente de datos que la vista web)
                    var datos = await ObtenerDatosRitmoInscripciones(idEvento, idSubtipo, fechaFiltrar, con);

                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.Worksheets.Add("Avance de Pagos");

                        string[] headers = { "Nombre Asistente", "Inscrito Por", "Modalidad", "Costo Total", "Monto Pagado al Corte", "% Avance", "Estado" };
                        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

                        int row = 2;
                        foreach (var item in datos)
                        {
                            ws.Cell(row, 1).Value = item.Asistente;
                            ws.Cell(row, 2).Value = item.Inscriptor;
                            ws.Cell(row, 3).Value = item.Modalidad;
                            ws.Cell(row, 4).Value = item.CostoOficial;
                            ws.Cell(row, 5).Value = item.MontoPagadoAlCorte;
                            ws.Cell(row, 6).Value = item.CostoOficial > 0 ? (item.MontoPagadoAlCorte / item.CostoOficial) : 1;

                            bool cumpleMeta = item.PorcentajeAlcanzado >= porcentajeMeta;
                            ws.Cell(row, 7).Value = cumpleMeta ? "META CUMPLIDA" : "INSUFICIENTE";
                            ws.Cell(row, 7).Style.Font.SetBold().Font.SetFontColor(cumpleMeta ? ClosedXML.Excel.XLColor.Green : ClosedXML.Excel.XLColor.Red);

                            ws.Cell(row, 4).Style.NumberFormat.Format = "$ #,##0.00";
                            ws.Cell(row, 5).Style.NumberFormat.Format = "$ #,##0.00";
                            ws.Cell(row, 6).Style.NumberFormat.Format = "0.0%";
                            row++;
                        }

                        var range = ws.Range(1, 1, 1, headers.Length);
                        range.Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);
                        ws.SheetView.FreezeRows(1);
                        ws.Columns().AdjustToContents();

                        using (var stream = new MemoryStream())
                        {
                            wb.SaveAs(stream);
                            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Ritmo_Inscripciones_{tituloEvento}_{fechaFiltrar:yyyyMMdd}.xlsx");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("RitmoInscripciones", new { sid = sid });
            }
        }


        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ApiObtenerDesglosePagos(int idAsistente)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Json(new { exito = false, mensaje = "Sin permisos." });

            decimal costoTotal = 0;

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    string sqlCosto = @"
                        SELECT COALESCE(SUM(CASE WHEN log.""Id_Log"" IS NOT NULL THEN log.""Monto_Original"" ELSE c.""Monto_Pagar"" END), 0)
                        FROM ""Eventos_C_Cuentas_Cobrar"" c
                        LEFT JOIN ""Eventos_Ingresos_Efectivo_Log"" log ON c.""Id_Cuenta"" = log.""Id_Cuenta""
                        WHERE c.""Id_Asistente"" = @idA";

                    using (var cmdC = new NpgsqlCommand(sqlCosto, con))
                    {
                        cmdC.Parameters.AddWithValue("@idA", idAsistente);
                        costoTotal = (decimal)await cmdC.ExecuteScalarAsync();
                    }

                    // 👉 LLAMADA AL HELPER (Cero duplicidad)
                    var pagos = await ObtenerHistorialPagosAsistente(idAsistente, con);

                    decimal totalPagado = pagos.Sum(x => x.MontoPagado);
                    decimal porcentaje = costoTotal > 0 ? Math.Round((totalPagado / costoTotal) * 100, 2) : 100;

                    return Json(new { exito = true, datos = pagos, totalPagado = totalPagado, costoTotal = costoTotal, porcentaje = porcentaje });
                }
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = ex.Message });
            }
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> DescargarDesgloseAsistenteExcel(int idAsistente)
        {
            if (!User.TienePermiso(Modulo, PermisoAdmin) && !User.TienePermiso(Modulo, PermisoEditar))
                return Forbid();

            string nombreAsistente = "Asistente";

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.Worksheets.Add("Desglose Individual");

                        string sqlH = @"SELECT b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as ""Mod"" FROM ""Eventos_B_Asistentes"" b LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo"" WHERE b.""Id_Asistente"" = @idA";
                        using (var cmdH = new NpgsqlCommand(sqlH, con))
                        {
                            cmdH.Parameters.AddWithValue("@idA", idAsistente);
                            using (var rH = await cmdH.ExecuteReaderAsync()) if (await rH.ReadAsync())
                            {
                                nombreAsistente = rH["Nombre_Completo"].ToString();
                                ws.Cell(1, 1).Value = "AUDITORÍA INDIVIDUAL DE PAGOS";
                                ws.Range("A1:E1").Merge().Style.Font.SetBold().Font.SetFontSize(13).Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#2c3e50")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);

                                ws.Cell(2, 1).Value = "Asistente:"; ws.Cell(2, 2).Value = nombreAsistente;
                                ws.Cell(3, 1).Value = "Modalidad:"; ws.Cell(3, 2).Value = rH["Mod"].ToString();
                            }
                        }

                        string[] headers = { "Fecha y Hora", "Concepto / Plazo", "Método", "Referencia", "Monto Validado" };
                        for (int i = 0; i < headers.Length; i++) ws.Cell(5, i + 1).Value = headers[i];
                        ws.Range("A5:E5").Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#34495e")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);

                        // 👉 LLAMADA AL HELPER (La misma fuente de datos que el Modal AJAX)
                        var pagos = await ObtenerHistorialPagosAsistente(idAsistente, con);

                        int row = 6;
                        decimal total = 0;
                        foreach (var p in pagos)
                        {
                            total += p.MontoPagado;
                            ws.Cell(row, 1).Value = p.FechaPago;
                            ws.Cell(row, 2).Value = p.Concepto;
                            ws.Cell(row, 3).Value = p.Metodo;
                            ws.Cell(row, 4).Value = p.Referencia;
                            ws.Cell(row, 5).Value = p.MontoPagado;
                            ws.Cell(row, 5).Style.NumberFormat.Format = "$ #,##0.00";
                            row++;
                        }

                        ws.Cell(row, 4).Value = "TOTAL VALIDADOS:";
                        ws.Cell(row, 4).Style.Font.SetBold().Alignment.SetHorizontal(ClosedXML.Excel.XLAlignmentHorizontalValues.Right);
                        ws.Cell(row, 5).Value = total;
                        ws.Cell(row, 5).Style.Font.SetBold().NumberFormat.Format = "$ #,##0.00";
                        ws.Cell(row, 5).Style.Border.TopBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                        ws.Cell(row, 5).Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Double;

                        ws.Columns().AdjustToContents();

                        using (var stream = new MemoryStream())
                        {
                            wb.SaveAs(stream);
                            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Desglose_Pagos_{nombreAsistente.Replace(" ", "_")}.xlsx");
                        }
                    }
                }
            }
            catch (Exception)
            {
                return BadRequest("Error al generar el reporte.");
            }
        }
        // =========================================================================================
        // NUEVAS VISTAS Y ENDPOINTS PARA LA ETAPA 2 Y ETAPA 3 (EXTRAS Y ESCANER QR)
        // =========================================================================================

        [Authorize]
        public async Task<IActionResult> GestorEventoGeneral(string redir)
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Forbid();
            
            ViewBag.RedirAccion = redir ?? "VerExtras";
            var eventos = new List<dynamic>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Id_Evento"", ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Activo""=TRUE ORDER BY ""Fecha_Inicio"" DESC", conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) eventos.Add(new { Id = Funciones.EncriptarId((int)r["Id_Evento"]), Titulo = r["Titulo"].ToString() });
                }
            }
            catch { }
            return View(eventos); // Vista GestorEventoGeneral.cshtml
        }

        [Authorize]
        public async Task<IActionResult> VerExtras(string sid)
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Forbid();
                
            if (string.IsNullOrEmpty(sid)) return RedirectToAction("GestorEventoGeneral", new { redir = "VerExtras" });

            ViewBag.Sid = sid;
            try
            {
                int id = Funciones.DesencriptarId(sid);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento""=@id", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        ViewBag.TituloEvento = (await cmd.ExecuteScalarAsync())?.ToString();
                    }
                    
                    var productos = new List<dynamic>();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Id_ProductoExtra"", ""Nombre_Producto"" FROM ""Eventos_Catalogo_ProductosExtra"" WHERE ""Id_Evento""=@id ORDER BY ""Nombre_Producto""", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                            while (await r.ReadAsync()) productos.Add(new { Id = (int)r["Id_ProductoExtra"], Nombre = r["Nombre_Producto"].ToString() });
                    }
                    ViewBag.Productos = productos;
                }
            }
            catch { return RedirectToAction("Index"); }
            
            return View();
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ObtenerExtrasJson(string sid, int idProducto = 0, string estado = "todos", string buscar = "")
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Unauthorized();

            int idEvento;
            try { idEvento = Funciones.DesencriptarId(sid); } catch { return BadRequest(); }

            var resultados = new List<dynamic>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"
                        SELECT b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                               pe_cat.""Nombre_Producto"", pe.""Cantidad"", pe.""Precio_Unitario"", pe.""Total"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as ""PagosHechos"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as ""TotalPagos""
                        FROM ""Eventos_Asistentes_ProductosExtra"" pe
                        JOIN ""Eventos_Catalogo_ProductosExtra"" pe_cat ON pe.""Id_ProductoExtra"" = pe_cat.""Id_ProductoExtra""
                        JOIN ""Eventos_B_Asistentes"" b ON pe.""Id_Asistente"" = b.""Id_Asistente""
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                        WHERE r.""Id_Evento"" = @idEv";

                    if (idProducto > 0) sql += @" AND pe.""Id_ProductoExtra"" = @idP";
                    if (!string.IsNullOrEmpty(buscar)) sql += @" AND b.""Nombre_Completo"" ILIKE @buscar";
                    sql += @" ORDER BY pe_cat.""Nombre_Producto"", b.""Nombre_Completo""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idEv", idEvento);
                        if (idProducto > 0) cmd.Parameters.AddWithValue("@idP", idProducto);
                        if (!string.IsNullOrEmpty(buscar)) cmd.Parameters.AddWithValue("@buscar", "%" + buscar + "%");

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                int pagosHechos = Convert.ToInt32(reader["PagosHechos"]);
                                int totalPagos = Convert.ToInt32(reader["TotalPagos"]);
                                string estadoPago = pagosHechos == totalPagos && totalPagos > 0 ? "LIQUIDADO" : (pagosHechos > 0 ? "ABONADO" : "DEUDA");

                                if (estado == "pagado" && estadoPago != "LIQUIDADO") continue;
                                if (estado == "deuda" && estadoPago == "LIQUIDADO") continue;

                                resultados.Add(new
                                {
                                    asistente = reader["Nombre_Completo"].ToString(),
                                    modalidad = reader["Modalidad"].ToString(),
                                    producto = reader["Nombre_Producto"].ToString(),
                                    cantidad = (int)reader["Cantidad"],
                                    total = (decimal)reader["Total"],
                                    estadoPago = estadoPago
                                });
                            }
                        }
                    }
                }
                return Json(new { success = true, data = resultados });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // =========================================================================================
        // ETAPA 3: ESCÁNER QR EN PUERTA
        // =========================================================================================

        [Authorize]
        public async Task<IActionResult> EscanerQR(string sid)
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Forbid();
                
            if (string.IsNullOrEmpty(sid)) return RedirectToAction("GestorEventoGeneral", new { redir = "EscanerQR" });

            ViewBag.Sid = sid;
            try
            {
                int id = Funciones.DesencriptarId(sid);
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Eventos_Catalogo"" WHERE ""Id_Evento""=@id", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        ViewBag.TituloEvento = (await cmd.ExecuteScalarAsync())?.ToString();
                    }
                }
            }
            catch { return RedirectToAction("Index"); }
            
            return View();
        }

        public class QrRequest { public string sid { get; set; } public string token { get; set; } public int idCuenta { get; set; } }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> ConsultarDetalleQRJson([FromBody] QrRequest req)
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Unauthorized();

            try
            {
                int idEvento = Funciones.DesencriptarId(req.sid);
                var asistentes = new List<dynamic>();

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    
                    // Verificamos si existen las columnas Asistencia y Notas_Staff, si no, las devolvemos falsas/nulas simuladas
                    bool tieneAsistencia = true;
                    try { 
                        using (var cmd = new NpgsqlCommand(@"SELECT ""Asistencia"" FROM ""Eventos_B_Asistentes"" LIMIT 1", conexion)) { await cmd.ExecuteScalarAsync(); }
                    } catch { tieneAsistencia = false; }

                    string sqlAsistencia = tieneAsistencia ? @"b.""Asistencia"", b.""Notas_Staff""" : @"FALSE as ""Asistencia"", '' as ""Notas_Staff""";

                    string sql = $@"
                        SELECT b.""Id_Asistente"", b.""Nombre_Completo"", COALESCE(s.""Nombre"", 'Entrada General') as ""Modalidad"",
                               {sqlAsistencia},
                               e.""Id_Encuesta_Requisito"",
                               COALESCE((SELECT COUNT(1) FROM ""Encuestas_Respuestas_Header"" h 
                                         WHERE h.""Id_Encuesta"" = e.""Id_Encuesta_Requisito"" 
                                           AND h.""Id_Asistente_Evento"" = b.""Id_Asistente""), 0) > 0 AS ""EncuestaRespondida"",
                               (SELECT string_agg(c_pe.""Nombre_Producto"" || ' (x' || pe.""Cantidad"" || ')' || CASE WHEN c_pe.""Descripcion"" IS NOT NULL AND c_pe.""Descripcion"" <> '' THEN ' - ' || c_pe.""Descripcion"" ELSE '' END, ', ') 
                                FROM ""Eventos_Asistentes_ProductosExtra"" pe 
                                JOIN ""Eventos_Catalogo_ProductosExtra"" c_pe ON pe.""Id_ProductoExtra"" = c_pe.""Id_ProductoExtra""
                                WHERE pe.""Id_Asistente"" = b.""Id_Asistente"") as ""Extras"",
                               (SELECT string_agg(q.""Texto_Pregunta"" || ': ' || rp.""Valor_Respuesta"", ' | ')
                                FROM ""Eventos_Respuestas"" rp
                                JOIN ""Eventos_Preguntas"" q ON rp.""Id_Pregunta"" = q.""Id_Pregunta""
                                WHERE rp.""Id_Asistente"" = b.""Id_Asistente"") as ""Preguntas"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = TRUE) as ""PagosHechos"",
                               (SELECT COUNT(*) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"") as ""TotalPagos"",
                               (SELECT COALESCE(SUM(c.""Monto_Pagar""), 0) FROM ""Eventos_C_Cuentas_Cobrar"" c WHERE c.""Id_Asistente"" = b.""Id_Asistente"" AND c.""Pagado"" = FALSE) as ""MontoPendiente""
                        FROM ""Eventos_B_Asistentes"" b
                        JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro""
                        JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                        LEFT JOIN ""Eventos_Subtipos"" s ON b.""Id_Subtipo"" = s.""Id_Subtipo""
                        WHERE r.""Id_Evento"" = @idEv";

                    bool tieneResultados = false;

                    if (req.idCuenta > 0)
                    {
                        string sqlIndividual = sql + @" AND b.""Id_Asistente"" IN (SELECT ""Id_Asistente"" FROM ""Eventos_C_Cuentas_Cobrar"" WHERE ""Id_Cuenta"" = @idCuenta)";
                        using (var cmd = new NpgsqlCommand(sqlIndividual, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idEv", idEvento);
                            cmd.Parameters.AddWithValue("@idCuenta", req.idCuenta);
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync()) { tieneResultados = true; asistentes.Add(ExtraerDatosQR(reader)); }
                            }
                        }
                    }

                    if (!tieneResultados && !string.IsNullOrEmpty(req.token))
                    {
                        string tokenLimpio = req.token.Trim();
                        int idAsisDirecto = 0;
                        if (tokenLimpio.StartsWith("ASI-", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(tokenLimpio.Substring(4), out idAsisDirecto);
                        }
                        else
                        {
                            int.TryParse(tokenLimpio, out idAsisDirecto);
                        }

                        string sqlGeneral = sql + @" AND (
                            b.""Token_Pago_Externo"" = @token 
                            OR b.""Id_Asistente""::text = @token
                            " + (idAsisDirecto > 0 ? @" OR b.""Id_Asistente"" = @idAsisDirecto" : "") + @")";

                        using (var cmd = new NpgsqlCommand(sqlGeneral, conexion))
                        {
                            cmd.Parameters.AddWithValue("@idEv", idEvento);
                            cmd.Parameters.AddWithValue("@token", tokenLimpio);
                            if (idAsisDirecto > 0) cmd.Parameters.AddWithValue("@idAsisDirecto", idAsisDirecto);

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync()) { tieneResultados = true; asistentes.Add(ExtraerDatosQR(reader)); }
                            }
                        }
                    }

                    if (!tieneResultados) return Json(new { success = false, message = "El recibo o código QR escaneado no es válido para este evento." });

                    return Json(new { success = true, data = asistentes });
                }
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        private dynamic ExtraerDatosQR(NpgsqlDataReader reader)
        {
            int pagosHechos = Convert.ToInt32(reader["PagosHechos"]);
            int totalPagos = Convert.ToInt32(reader["TotalPagos"]);
            decimal montoPendiente = reader["MontoPendiente"] != DBNull.Value ? Convert.ToDecimal(reader["MontoPendiente"]) : 0;
            string estadoPago = pagosHechos == totalPagos && totalPagos > 0 ? "LIQUIDADO" : (pagosHechos > 0 ? "ABONADO" : "DEUDA");

            int idEncReq = reader["Id_Encuesta_Requisito"] != DBNull.Value ? Convert.ToInt32(reader["Id_Encuesta_Requisito"]) : 0;
            bool encuestaRespondida = idEncReq == 0 || (reader["EncuestaRespondida"] != DBNull.Value && (bool)reader["EncuestaRespondida"]);

            return new
            {
                idAsistente = reader["Id_Asistente"],
                nombre = reader["Nombre_Completo"].ToString(),
                modalidad = reader["Modalidad"].ToString(),
                asistencia = (bool)reader["Asistencia"],
                notas = reader["Notas_Staff"]?.ToString() ?? "",
                extras = reader["Extras"]?.ToString() ?? "Ninguno",
                preguntas = reader["Preguntas"]?.ToString() ?? "Sin respuestas",
                estadoPago = estadoPago,
                montoPendiente = montoPendiente,
                encuestaPendiente = !encuestaRespondida
            };
        }

        public class AsistenciaRequest { public int idAsistente { get; set; } public bool asistencia { get; set; } public string notas { get; set; } }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> GuardarNotaAsistenciaJson([FromBody] AsistenciaRequest req)
        {
            if (!User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Admin) && !User.TienePermiso(Parametros.Modulos.Eventos, Parametros.Permisos.Editar))
                return Unauthorized();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"UPDATE ""Eventos_B_Asistentes"" SET ""Asistencia""=@asis, ""Notas_Staff""=@notas WHERE ""Id_Asistente""=@id", conexion))
                    {
                        cmd.Parameters.AddWithValue("@asis", req.asistencia);
                        cmd.Parameters.AddWithValue("@notas", (object)req.notas ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@id", req.idAsistente);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                return Json(new { success = true });
            }
            catch (Exception ex) 
            { 
                return Json(new { success = false, message = "Error. Es necesario ejecutar los scripts de BD para habilitar la Asistencia." }); 
            }
        }
    }
}