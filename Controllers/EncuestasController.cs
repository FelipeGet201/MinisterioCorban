using ClosedXML.Excel;
using DocumentFormat.OpenXml.Drawing.Charts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using System.IO.Compression;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace RedAJP.Controllers
{
    [Authorize]
    public class EncuestasController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Encuestas;
        private readonly IWebHostEnvironment _env;
        private readonly Cloudinary _cloudinary; 

        public EncuestasController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
            _env = env;

            Account account = new Account(
                configuration["Cloudinary:CloudName"],
                configuration["Cloudinary:ApiKey"],
                configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        /// <summary>
        /// Función que se ejecuta al cargar el listado de encuestas para actualizar automáticamente el estado de las que han vencido.
        /// </summary>
        /// <returns>Retorna true si la actualización fue exitosa, false si ocurrió un error.</returns>
        private async Task<bool> ActualizarEstadosVencidos()
        {
            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    // Lógica: Si está Activa Y tiene Fecha Límite Y esa fecha ya pasó -> Poner Inactiva
                    string sql = @"UPDATE ""Encuestas_Catalogo"" 
                                   SET ""Activa"" = FALSE 
                                   WHERE ""Activa"" = TRUE 
                                   AND ""Fecha_Limite"" IS NOT NULL 
                                   AND ""Fecha_Limite"" < NOW()";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                return true; // Todo salió bien
            }
            catch (Exception)
            {
                return false; // Hubo un error
            }
        }

        /// <summary>
        /// Listado de encuestas con opción para mostrar cerradas. Al cargar esta vista, se ejecuta el mantenimiento automático para garantizar que el estado de las encuestas esté actualizado según su fecha límite. 
        /// Si el mantenimiento falla, se muestra un mensaje de error y se redirige al usuario para evitar inconsistencias. 
        /// Además, se incluyen datos para bitácora en cada carga del listado.
        /// </summary>
        /// <param name="verCerradas">Es un parámetro opcional que indica si se deben mostrar las encuestas cerradas (inactivas) en el listado. Por defecto es false, mostrando solo las activas.</param>
        /// <returns>Retorna la vista con el listado de encuestas, aplicando el filtro según el parámetro verCerradas.</returns>
        public async Task<IActionResult> Index(bool verCerradas = false)
        {
            // 1. Seguridad
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            // 2. Mantenimiento automático con validación de error
            bool mantenimientoOk = await ActualizarEstadosVencidos();
            if (!mantenimientoOk)
            {
                // Si falla el mantenimiento, sacamos al usuario para evitar inconsistencias
                MostrarMensaje("Error de Sistema", "Ocurrió un error al actualizar los estados de las encuestas. Por favor reporte este incidente a soporte.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<EncuestaIndexViewModel>();

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    string filtroSQL = verCerradas ? "" : @" WHERE e.""Activa"" = TRUE";

                    // JOIN con Grupos para obtener el nombre si aplica
                    string sql = $@"
                    SELECT e.*,
                           (SELECT COUNT(*) FROM ""Encuestas_Respuestas_Header"" h WHERE h.""Id_Encuesta"" = e.""Id_Encuesta"") as ""Total"",
                           g.""Nombre_Grupo""
                    FROM ""Encuestas_Catalogo"" e
                    LEFT JOIN ""Sist_Grupos_Whatsapp"" g ON e.""Id_Grupo_Acceso"" = g.""Id_Grupo""
                    {filtroSQL}
                    ORDER BY e.""Fecha_Creacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            lista.Add(new EncuestaIndexViewModel
                            {
                                Id = (int)r["Id_Encuesta"],
                                Titulo = r["Titulo"].ToString(),
                                ClaveUrl = r["Clave_Url"].ToString(),
                                Estado = (bool)r["Activa"] ? "Activa" : "Inactiva",
                                EsAnonima = (bool)r["Es_Anonima"],
                                EsPrivada = (bool)r["Es_Privada"],
                                GrupoAcceso = r["Nombre_Grupo"]?.ToString(), // <--- ASIGNAMOS GRUPO
                                TotalRespuestas = Convert.ToInt32(r["Total"]),
                                FechaCreacion = (DateTime)r["Fecha_Creacion"]
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            ViewBag.VerCerradas = verCerradas;
            return View(lista);
        }

        /// <summary>
        /// Vista para crear o editar una encuesta. 
        /// Si se proporciona un ID, se carga la encuesta existente para edición. 
        /// Si se proporciona idCopiar, se carga la encuesta como plantilla para crear una nueva (con ID 0).
        /// </summary>
        /// <param name="id">Es el identificador de la encuesta que se desea editar. Si es 0, se asume que se está creando una nueva encuesta desde cero.</param>
        /// <param name="idCopiar">Es un parámetro opcional que, si se proporciona, 
        /// indica que se desea copiar una encuesta existente. 
        /// El sistema cargará los datos de la encuesta indicada por idCopiar pero asignará un ID de 0 para crear una nueva encuesta basada en esa plantilla.</param>
        /// <returns>Retorna la vista del editor de encuestas, cargando los datos según el modo (edición o copia) y aplicando las restricciones necesarias si la encuesta tiene respuestas.</returns>
        public async Task<IActionResult> Editor(int id = 0, int? idCopiar = null)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tiene permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new EncuestaEditorViewModel { Id = id };

            // Valores por defecto importantes
            modelo.Activa = true;
            modelo.TieneRespuestas = false; // Por defecto false

            await CargarCatalogosViewBag();

            int idBusqueda = id > 0 ? id : (idCopiar ?? 0);

            if (idBusqueda > 0)
            {
                try
                {
                    using (var con = new NpgsqlConnection(_cadenaConexion))
                    {
                        await con.OpenAsync();

                        // 1. VERIFICAR SI HAY RESPUESTAS (SOLO EN EDICIÓN)
                        // Si es Copia (idCopiar), no verificamos porque la nueva encuesta nace vacía.
                        if (id > 0)
                        {
                            string sqlCheck = "SELECT COUNT(1) FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Encuesta\" = @id";
                            using (var cmd = new NpgsqlCommand(sqlCheck, con))
                            {
                                cmd.Parameters.AddWithValue("@id", id);
                                long total = (long)await cmd.ExecuteScalarAsync();
                                modelo.TieneRespuestas = (total > 0); // <--- ESTO ACTIVA EL BLOQUEO EN LA VISTA
                            }

                            string sqlCheckReq = "SELECT COUNT(1) FROM \"Eventos_Catalogo\" WHERE \"Id_Encuesta_Requisito\" = @id";
                            using (var cmdReq = new NpgsqlCommand(sqlCheckReq, con))
                            {
                                cmdReq.Parameters.AddWithValue("@id", id);
                                long totalReq = (long)await cmdReq.ExecuteScalarAsync();
                                modelo.EsRequisitoDeEvento = (totalReq > 0); 
                            }
                        }

                        // 2. CARGAR ENCABEZADO
                        string sqlHeader = @"SELECT * FROM ""Encuestas_Catalogo"" WHERE ""Id_Encuesta"" = @id";
                        using (var cmd = new NpgsqlCommand(sqlHeader, con))
                        {
                            cmd.Parameters.AddWithValue("@id", idBusqueda);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.Descripcion = r["Descripcion"]?.ToString();
                                    modelo.EsAnonima = (bool)r["Es_Anonima"];
                                    modelo.EsPrivada = (bool)r["Es_Privada"];
                                    modelo.IdGrupoAcceso = r["Id_Grupo_Acceso"] as int?;

                                    // Cargar si solicita iglesia
                                    modelo.SolicitarIglesia = r["Solicitar_Iglesia"] != DBNull.Value ? (bool)r["Solicitar_Iglesia"] : false;

                                    if (id > 0) // MODO EDICIÓN
                                    {
                                        modelo.Titulo = r["Titulo"].ToString();
                                        modelo.ClaveUrl = r["Clave_Url"].ToString();
                                        modelo.Activa = (bool)r["Activa"];
                                        modelo.FechaLimite = r["Fecha_Limite"] as DateTime?;
                                    }
                                    else // MODO COPIA (DUPLICAR)
                                    {
                                        modelo.Id = 0; // Nueva ID al guardar
                                        modelo.Titulo = r["Titulo"].ToString() + " (Copia)";
                                        modelo.ClaveUrl = GenerarClaveAleatoria();
                                        modelo.Activa = false; // Nace inactiva
                                        modelo.FechaLimite = null;
                                        modelo.TieneRespuestas = false; // La copia no tiene respuestas
                                        MostrarMensaje("Modo Plantilla", "Copia cargada. Define título y guarda.", TipoMensaje.Info);
                                    }
                                }
                                else return RedirectToAction("Index");
                            }
                        }

                        // 3. CARGAR PREGUNTAS
                        string sqlP = @"SELECT * FROM ""Encuestas_Preguntas"" WHERE ""Id_Encuesta"" = @id ORDER BY ""Orden"" ASC";
                        using (var cmd = new NpgsqlCommand(sqlP, con))
                        {
                            cmd.Parameters.AddWithValue("@id", idBusqueda);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    modelo.Preguntas.Add(new PreguntaEncuestaItem
                                    {
                                        // Si es copia, Id_Pregunta debe ser 0 para que se inserten como nuevas
                                        Id_Pregunta = (id > 0) ? (int)r["Id_Pregunta"] : 0,
                                        Texto = r["Texto"].ToString(),
                                        Tipo = r["Id_Tipo"].ToString(),
                                        Configuracion = r["Configuracion"]?.ToString(),
                                        Requerida = (bool)r["Requerida"],
                                        Orden = (int)r["Orden"],
                                        Eliminada = false
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                    return RedirectToAction("Index");
                }
            }
            else
            {
                // NUEVA ENCUESTA DESDE CERO
                modelo.ClaveUrl = GenerarClaveAleatoria();
                modelo.Preguntas.Add(new PreguntaEncuestaItem { Id_Pregunta = 0, Tipo = "Texto", Texto = "", Orden = 1 });
            }

            return View(modelo);
        }

        /// <summary>
        /// Función para cargar los catálogos necesarios en el ViewBag para el editor de encuestas, como los tipos de pregunta y los grupos de acceso.
        /// </summary>
        /// <returns>Retorna una tarea asincrónica que carga los datos en el ViewBag para ser utilizados en las vistas del editor de encuestas.</returns>
        private async Task CargarCatalogosViewBag()
        {
            var tipos = new List<dynamic>();
            var grupos = new List<dynamic>();
            using (var con = new NpgsqlConnection(_cadenaConexion))
            {
                await con.OpenAsync();
                using (var cmd = new NpgsqlCommand("SELECT * FROM \"Encuestas_Tipos_Pregunta\"", con))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) tipos.Add(new { Id = r["Id_Tipo"].ToString(), Nombre = r["Descripcion"].ToString(), UsaOpciones = (bool)r["Tiene_Opciones"] });

                try
                {
                    using (var cmd = new NpgsqlCommand("SELECT \"Id_Grupo\", \"Nombre_Grupo\" FROM \"Sist_Grupos_Whatsapp\" WHERE \"Activo\"=TRUE", con))
                    using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) grupos.Add(new { Id = (int)r["Id_Grupo"], Nombre = r["Nombre_Grupo"].ToString() });
                }
                catch { }
            }
            ViewBag.TiposPregunta = tipos;
            ViewBag.Grupos = grupos;
        }

        /// <summary>
        /// Genera una clave aleatoria para la URL de la encuesta, combinando la fecha actual (día y mes) con un número aleatorio de tres dígitos.
        /// </summary>
        /// <returns>Retorna una cadena de texto que representa la clave aleatoria generada, 
        /// con el formato "ddMM-XXX" donde dd es el día, 
        /// MM es el mes y XXX es un número aleatorio entre 100 y 999.</returns>
        private string GenerarClaveAleatoria()
        {
            string fecha = DateTime.Now.ToString("ddMM");
            string random = new Random().Next(100, 999).ToString();
            return $"{fecha}-{random}";
        }

        /// <summary>
        /// Función para guardar la encuesta, tanto en modo creación como edición.
        /// </summary>
        /// <param name="modelo">Es un objeto de tipo EncuestaEditorViewModel que contiene los datos de la encuesta a guardar, incluyendo el encabezado y las preguntas.
        /// <returns>Retorna una acción que redirige al listado de encuestas si el guardado es exitoso, o retorna la vista del editor con un mensaje de error si ocurre alguna excepción durante el proceso de guardado.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EncuestaEditorViewModel modelo)
        {
            // 1. Validar Permisos
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tiene permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            // 2. Limpieza y defaults
            modelo.ClaveUrl = modelo.ClaveUrl?.Trim().Replace(" ", "");
            if (string.IsNullOrEmpty(modelo.ClaveUrl))
                modelo.ClaveUrl = Guid.NewGuid().ToString().Substring(0, 8);

            int idUsuarioLogueado = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                // 3. Lógica Inteligente de Fechas
                // Si se activa y ya venció, sumamos 1 día desde hoy
                if (modelo.Activa && modelo.FechaLimite.HasValue && modelo.FechaLimite.Value < DateTime.Now)
                {
                    modelo.FechaLimite = DateTime.Now.AddDays(1);
                }

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    bool tieneRespuestas = false;
                    bool esRequisito = false;
                    if (modelo.Id > 0)
                    {
                        using (var cmd = new NpgsqlCommand("SELECT COUNT(1) FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Encuesta\"=@id", con))
                        {
                            cmd.Parameters.AddWithValue("@id", modelo.Id);
                            tieneRespuestas = ((long)await cmd.ExecuteScalarAsync()) > 0;
                        }

                        using (var cmd = new NpgsqlCommand("SELECT COUNT(1) FROM \"Eventos_Catalogo\" WHERE \"Id_Encuesta_Requisito\"=@id", con))
                        {
                            cmd.Parameters.AddWithValue("@id", modelo.Id);
                            esRequisito = ((long)await cmd.ExecuteScalarAsync()) > 0;
                        }
                    }

                    if (esRequisito && !modelo.EsPrivada)
                    {
                        MostrarMensaje("Bloqueado", "No puedes desactivar el Inicio de Sesión Obligatorio porque esta encuesta es requisito de un Evento.", TipoMensaje.Error);
                        return RedirectToAction("Editor", new { id = modelo.Id });
                    }

                    using (var trans = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            int idEncuesta = modelo.Id;

                            // ---------------------------------------------------------
                            // A. GUARDAR ENCABEZADO (Siempre editable)
                            // ---------------------------------------------------------
                            if (idEncuesta == 0)
                            {
                                string sqlInsert = @"INSERT INTO ""Encuestas_Catalogo"" 
                            (""Titulo"", ""Descripcion"", ""Clave_Url"", ""Activa"", ""Fecha_Limite"", 
                             ""Es_Privada"", ""Id_Grupo_Acceso"", ""Es_Anonima"", ""Fecha_Creacion"", ""Solicitar_Iglesia"")
                            VALUES 
                            (@tit, @desc, @url, @act, @lim, @priv, @grp, @anon, NOW(), @solIg) 
                            RETURNING ""Id_Encuesta""";

                                using (var cmd = new NpgsqlCommand(sqlInsert, con, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", modelo.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)modelo.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@url", modelo.ClaveUrl);
                                    cmd.Parameters.AddWithValue("@act", modelo.Activa);
                                    cmd.Parameters.AddWithValue("@lim", (object)modelo.FechaLimite ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@priv", modelo.EsPrivada);
                                    cmd.Parameters.AddWithValue("@grp", (object)modelo.IdGrupoAcceso ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@anon", modelo.EsAnonima);
                                    cmd.Parameters.AddWithValue("@solIg", modelo.SolicitarIglesia); // NUEVO

                                    idEncuesta = (int)await cmd.ExecuteScalarAsync();
                                }
                            }
                            else
                            {
                                string sqlUpdate = @"UPDATE ""Encuestas_Catalogo"" SET 
                            ""Titulo""=@tit, ""Descripcion""=@desc, ""Clave_Url""=@url, 
                            ""Activa""=@act, ""Fecha_Limite""=@lim, 
                            ""Es_Privada""=@priv, ""Id_Grupo_Acceso""=@grp, ""Es_Anonima""=@anon,
                            ""Solicitar_Iglesia""=@solIg
                            WHERE ""Id_Encuesta""=@id";

                                using (var cmd = new NpgsqlCommand(sqlUpdate, con, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", modelo.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)modelo.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@url", modelo.ClaveUrl);
                                    cmd.Parameters.AddWithValue("@act", modelo.Activa);
                                    cmd.Parameters.AddWithValue("@lim", (object)modelo.FechaLimite ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@priv", modelo.EsPrivada);
                                    cmd.Parameters.AddWithValue("@grp", (object)modelo.IdGrupoAcceso ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@anon", modelo.EsAnonima);
                                    cmd.Parameters.AddWithValue("@solIg", modelo.SolicitarIglesia); // NUEVO
                                    cmd.Parameters.AddWithValue("@id", idEncuesta);

                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            // ---------------------------------------------------------
                            // B. GUARDAR PREGUNTAS (Solo si NO hay respuestas)
                            // ---------------------------------------------------------
                            if (!tieneRespuestas)
                            {
                                // 1. Borrar todas las preguntas anteriores de esta encuesta
                                using (var cmd = new NpgsqlCommand("DELETE FROM \"Encuestas_Preguntas\" WHERE \"Id_Encuesta\"=@id", con, trans))
                                {
                                    cmd.Parameters.AddWithValue("@id", idEncuesta);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // 2. Insertar las nuevas (Filtrando las marcadas como Eliminadas)
                                if (modelo.Preguntas != null && modelo.Preguntas.Any())
                                {
                                    string sqlInsP = @"INSERT INTO ""Encuestas_Preguntas"" 
                                (""Id_Encuesta"", ""Texto"", ""Id_Tipo"", ""Configuracion"", ""Requerida"", ""Orden"")
                                VALUES 
                                (@id, @txt, @tipo, @conf, @req, @ord)";

                                    int orden = 1;
                                    foreach (var p in modelo.Preguntas)
                                    {
                                        // REGLA: Ignoramos si viene marcada como eliminada o no tiene texto
                                        if (p.Eliminada || string.IsNullOrWhiteSpace(p.Texto)) continue;

                                        using (var cmd = new NpgsqlCommand(sqlInsP, con, trans))
                                        {
                                            cmd.Parameters.AddWithValue("@id", idEncuesta);
                                            cmd.Parameters.AddWithValue("@txt", p.Texto);
                                            cmd.Parameters.AddWithValue("@tipo", p.Tipo);
                                            cmd.Parameters.AddWithValue("@conf", (object)p.Configuracion ?? "");
                                            cmd.Parameters.AddWithValue("@req", p.Requerida);
                                            cmd.Parameters.AddWithValue("@ord", orden++); // El orden se recalcula aquí secuencialmente

                                            await cmd.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // ---------------------------------------------------------
                            // C. BITÁCORA Y CONFIRMACIÓN
                            // ---------------------------------------------------------
                            string detalle = $"Gestión de encuesta ID {idEncuesta}. Título: {modelo.Titulo}";

                            await Funciones.RegistrarBitacora(con, idUsuarioLogueado, Modulo, modelo.Id == 0 ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar, detalle,
                                HttpContext.Connection.RemoteIpAddress?.ToString(), trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Éxito", "Encuesta guardada correctamente.", TipoMensaje.Exito);
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return View("Editor", modelo);
            }
        }

        /// <summary>
        /// Función para recargar la vista del editor de encuestas en caso de error, asegurando que los catálogos necesarios estén cargados en el ViewBag para evitar errores adicionales al mostrar la vista.
        /// </summary>
        /// <param name="modelo">Es un objeto de tipo EncuestaEditorViewModel que contiene los datos de la encuesta que se desea mostrar en la vista del editor, 
        /// incluyendo cualquier información que el usuario haya ingresado antes de que ocurriera el error.</param>
        /// <returns>Retorna la vista del editor de encuestas con el modelo proporcionado, 
        /// después de cargar los catálogos necesarios en el ViewBag para garantizar que la vista se muestre correctamente incluso después de un error.</returns>
        private async Task<IActionResult> RecargarVistaError(EncuestaEditorViewModel modelo) { await CargarCatalogosViewBag(); return View("Editor", modelo); }

        /// <summary>
        /// Función auxiliar para establecer los parámetros comunes de la encuesta en un comando SQL, utilizada tanto para inserción como para actualización,
        /// </summary>
        /// <param name="cmd">Es un objeto de tipo NpgsqlCommand al que se le agregarán los parámetros necesarios para guardar o actualizar una encuesta en la base de datos</param>
        /// <param name="m">Es un objeto de tipo EncuestaEditorViewModel que contiene los datos de la encuesta que se desean guardar o actualizar</param>
        private void SetParamsEncuesta(NpgsqlCommand cmd, EncuestaEditorViewModel m)
        {
            cmd.Parameters.AddWithValue("@tit", m.Titulo);
            cmd.Parameters.AddWithValue("@desc", (object)m.Descripcion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@clave", m.ClaveUrl);
            cmd.Parameters.AddWithValue("@act", m.Activa);
            cmd.Parameters.AddWithValue("@anon", m.EsAnonima);
            cmd.Parameters.AddWithValue("@priv", m.EsPrivada);
            cmd.Parameters.AddWithValue("@grp", (object)m.IdGrupoAcceso ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lim", (object)m.FechaLimite ?? DBNull.Value);
        }

        /// <summary>
        /// Función para mostrar la vista de respuesta de la encuesta al usuario final, 
        /// aplicando todas las validaciones necesarias para garantizar que solo los usuarios autorizados puedan acceder a encuestas privadas,
        /// </summary>
        /// <param name="clave">Es un parámetro de tipo cadena que representa la clave única de la encuesta que se desea responder, 
        /// utilizada para identificar y cargar la encuesta correspondiente desde la base de datos.</param>
        /// <returns>Retorna la vista para responder la encuesta, después de realizar todas las validaciones de acceso,
        /// estado y duplicidad necesarias para garantizar una experiencia segura y consistente para el usuario final.</returns>
        [AllowAnonymous]
        [Route("E/{clave}")]
        public async Task<IActionResult> Responder(string clave, [FromQuery] string evt = null, [FromQuery] int? asis = null, [FromQuery] string retorno = null)
        {
            // Ejecutamos barrido antes de cargar la encuesta para garantizar que el estado esté correcto incluso si nadie entra al admin       
            bool mantenimientoOk = await ActualizarEstadosVencidos();
            if (!mantenimientoOk)
            {
                // Si falla el mantenimiento, sacamos al usuario para evitar inconsistencias
                MostrarMensaje("Error de Sistema", "Ocurrió un error al actualizar los estados de las encuestas. Por favor reporte este incidente a soporte.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var modelo = new EncuestaResponderViewModel();
            modelo.IdEventoRetorno = evt;
            modelo.ClaveUrl = clave;
            modelo.Retorno = retorno;
            bool activa = false;
            bool esPrivada = false;
            int? idGrupoAcceso = null;
            int idEncuesta = 0;

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // 1. Obtener datos de la encuesta
                    string sql = "SELECT * FROM \"Encuestas_Catalogo\" WHERE \"Clave_Url\"=@clave";
                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@clave", clave);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                idEncuesta = (int)r["Id_Encuesta"];
                                modelo.Id = idEncuesta;
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.EsAnonima = (bool)r["Es_Anonima"];
                                modelo.SolicitarIglesia = r["Solicitar_Iglesia"] != DBNull.Value ? (bool)r["Solicitar_Iglesia"] : false;
                                activa = (bool)r["Activa"];
                                esPrivada = (bool)r["Es_Privada"];
                                idGrupoAcceso = r["Id_Grupo_Acceso"] as int?;
                            }
                            else return NotFound();
                        }
                    }

                    if (!activa) return View("EncuestaCerrada", modelo);

                    // 1.5 Validar si la encuesta es requisito de algún evento activo
                    int idEventoAsociado = 0;
                    string sqlEvtReq = @"SELECT e.""Id_Evento"" FROM ""Eventos_Catalogo"" e WHERE e.""Id_Encuesta_Requisito"" = @idEnc AND e.""Activo"" = TRUE LIMIT 1";
                    using (var cmdEvt = new NpgsqlCommand(sqlEvtReq, con))
                    {
                        cmdEvt.Parameters.AddWithValue("@idEnc", idEncuesta);
                        var resEvt = await cmdEvt.ExecuteScalarAsync();
                        if (resEvt != null && resEvt != DBNull.Value) idEventoAsociado = Convert.ToInt32(resEvt);
                    }

                    // 1.6 Validación del Asistente (Parámetro asis o ingreso manual)
                    if (asis.HasValue && asis.Value > 0)
                    {
                        string sqlAsis = @"SELECT b.""Id_Asistente"", b.""Nombre_Completo"", b.""Token_Pago_Externo"", r.""Id_Evento"" 
                                           FROM ""Eventos_B_Asistentes"" b 
                                           JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                           JOIN ""Eventos_Catalogo"" e ON r.""Id_Evento"" = e.""Id_Evento""
                                           WHERE b.""Id_Asistente"" = @asis AND e.""Id_Encuesta_Requisito"" = @idEnc LIMIT 1";
                        using (var cmdAsis = new NpgsqlCommand(sqlAsis, con))
                        {
                            cmdAsis.Parameters.AddWithValue("@asis", asis.Value);
                            cmdAsis.Parameters.AddWithValue("@idEnc", idEncuesta);
                            using (var rAsis = await cmdAsis.ExecuteReaderAsync())
                            {
                                if (await rAsis.ReadAsync())
                                {
                                    modelo.IdAsistenteEvento = (int)rAsis["Id_Asistente"];
                                    modelo.NombreAsistente = rAsis["Nombre_Completo"].ToString();
                                    modelo.TokenAsistente = rAsis["Token_Pago_Externo"].ToString();
                                    modelo.PedirDatosIdentidad = false;
                                }
                                else
                                {
                                    modelo.ErrorClave = $"No se encontró ningún asistente registrado con el ID #{asis.Value} para este evento.";
                                    modelo.RequiereClaveManual = true;
                                }
                            }
                        }

                        // Si encontramos al asistente, verificar si ya respondió su encuesta
                        if (modelo.IdAsistenteEvento.HasValue)
                        {
                            string sqlDupAsis = @"SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" 
                                                  WHERE ""Id_Encuesta"" = @id AND ""Id_Asistente_Evento"" = @asId LIMIT 1";
                            using (var cmdDupAsis = new NpgsqlCommand(sqlDupAsis, con))
                            {
                                cmdDupAsis.Parameters.AddWithValue("@id", idEncuesta);
                                cmdDupAsis.Parameters.AddWithValue("@asId", modelo.IdAsistenteEvento.Value);
                                var objRespAsis = await cmdDupAsis.ExecuteScalarAsync();
                                if (objRespAsis != null)
                                {
                                    ViewBag.Folio = ((int)objRespAsis).ToString("D6");
                                    ViewBag.EsAnonima = modelo.EsAnonima;
                                    ViewBag.TokenAsistente = modelo.TokenAsistente;
                                    ViewBag.Retorno = modelo.Retorno;
                                    return View("EncuestaResuelta", modelo);
                                }
                            }
                        }
                    }
                    else if (idEventoAsociado > 0)
                    {
                        // La encuesta es de un evento activo y no viene asistente especificado: pedir ID como clave de acceso
                        modelo.RequiereClaveManual = true;
                    }

                    // Cargar lista de iglesias si la encuesta lo requiere ---
                    if (modelo.SolicitarIglesia)
                    {
                        var listaIglesias = new List<dynamic>();
                        string sqlIglesias = @"
                            SELECT i.id, i.nombre, m.nombre as municipio, i.localidad 
                            FROM iciar_iglesias i 
                            LEFT JOIN iciar_municipios m ON i.municipio_id = m.id 
                            ORDER BY m.nombre ASC, i.nombre ASC";

                        using (var cmdIg = new NpgsqlCommand(sqlIglesias, con))
                        using (var rIg = await cmdIg.ExecuteReaderAsync())
                        {
                            while (await rIg.ReadAsync())
                            {
                                listaIglesias.Add(new
                                {
                                    Id = (int)rIg["id"],
                                    Nombre = rIg["nombre"].ToString(),
                                    Municipio = rIg["municipio"]?.ToString() ?? "",
                                    Localidad = rIg["localidad"]?.ToString() ?? "" // <--- AGREGADO AQUÍ
                                });
                            }
                        }
                        ViewBag.Iglesias = listaIglesias;
                    }

                    // 2. Validaciones de Acceso y Duplicidad
                    bool estaLogueado = User.Identity.IsAuthenticated;
                    int? idUsuario = null;

                    if (estaLogueado)
                    {
                        var claim = User.FindFirst("IdUsuario");
                        if (claim != null && int.TryParse(claim.Value, out int uid)) idUsuario = uid;

                        modelo.NombreUsuarioLogueado = User.Identity.Name;
                        if (!modelo.IdAsistenteEvento.HasValue)
                        {
                            modelo.PedirDatosIdentidad = false;
                        }

                        // --- EXTRAER IGLESIA ASIGNADA DEL USUARIO ---
                        if (idUsuario.HasValue && modelo.SolicitarIglesia)
                        {
                            string sqlUserIg = "SELECT \"Id_Iglesia_Asignada\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\"=@uid";
                            using (var cmdUIg = new NpgsqlCommand(sqlUserIg, con))
                            {
                                cmdUIg.Parameters.AddWithValue("@uid", idUsuario.Value);
                                var resIg = await cmdUIg.ExecuteScalarAsync();
                                if (resIg != null && resIg != DBNull.Value)
                                {
                                    if (int.TryParse(resIg.ToString(), out int parsedIgId) && parsedIgId > 0)
                                    {
                                        ViewBag.IdIglesiaPreasignada = parsedIgId;
                                    }
                                }
                            }
                        }

                        // VALIDACIÓN DE DUPLICIDAD (Solo si no es de asistente o si no se validó ya por asistente)
                        if (!modelo.IdAsistenteEvento.HasValue)
                        {
                            string sqlDup = "SELECT \"Id_Respuesta\" FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Encuesta\"=@id AND \"Id_Usuario\"=@uid LIMIT 1";
                            using (var cmd = new NpgsqlCommand(sqlDup, con))
                            {
                                cmd.Parameters.AddWithValue("@id", idEncuesta);
                                cmd.Parameters.AddWithValue("@uid", idUsuario.Value);
                                var objRespuesta = await cmd.ExecuteScalarAsync();
                                if (objRespuesta != null)
                                {
                                    ViewBag.Folio = ((int)objRespuesta).ToString("D6");
                                    ViewBag.EsAnonima = modelo.EsAnonima;
                                    return View("EncuestaResuelta", modelo);
                                }
                            }
                        }
                    }
                    else
                    {
                        modelo.PedirDatosIdentidad = !modelo.EsAnonima && !modelo.IdAsistenteEvento.HasValue;
                    }

                    // B) Validación de Privacidad (El token/Id de asistente exime de login privado)
                    if (esPrivada && !modelo.IdAsistenteEvento.HasValue)
                    {
                        if (!estaLogueado) return RedirectToAction("Index", "Login", new { returnUrl = $"/E/{clave}" });

                        if (idGrupoAcceso.HasValue && idUsuario.HasValue)
                        {
                            string sqlG = "SELECT COUNT(*) FROM \"Sist_Grupos_Miembros\" WHERE \"Id_Grupo\"=@grp AND \"Id_Usuario\"=@uid";
                            using (var cmdG = new NpgsqlCommand(sqlG, con))
                            {
                                cmdG.Parameters.AddWithValue("@grp", idGrupoAcceso.Value);
                                cmdG.Parameters.AddWithValue("@uid", idUsuario.Value);
                                if ((long)await cmdG.ExecuteScalarAsync() == 0)
                                {
                                    MostrarMensaje("Acceso Denegado", "No tienes permisos para acceder a esta encuesta privada.", TipoMensaje.Error);
                                    return RedirectToAction("Index", "Home");
                                }
                            }
                        }
                    }

                    // 3. Cargar Preguntas
                    string sqlP = @"SELECT * FROM ""Encuestas_Preguntas"" WHERE ""Id_Encuesta""=@id ORDER BY ""Orden"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlP, con))
                    {
                        cmd.Parameters.AddWithValue("@id", modelo.Id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Preguntas.Add(new PreguntaEncuestaItem
                                {
                                    Id_Pregunta = (int)r["Id_Pregunta"],
                                    Texto = r["Texto"].ToString(),
                                    Tipo = r["Id_Tipo"].ToString(),
                                    Configuracion = r["Configuracion"]?.ToString(),
                                    Requerida = (bool)r["Requerida"]
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { return BadRequest(ex.Message); }
            return View(modelo);
        }

        /// <summary>
        /// Función para cerrar (desactivar) una encuesta manualmente desde el panel de administración,
        /// </summary>
        /// <param name="id">Es un parámetro de tipo entero que representa el identificador único de la encuesta que se desea cerrar o desactivar manualmente desde el panel de administración,
        /// <returns>Retorna una acción que redirige al listado de encuestas después de intentar cerrar la encuesta especificada, 
        /// mostrando un mensaje de éxito si la operación fue exitosa o un mensaje de error si ocurrió alguna excepción durante el proceso.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cerrar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tiene permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    // Al desactivar manualmente, actualizamos fecha a NOW para consistencia
                    string sql = @"UPDATE ""Encuestas_Catalogo"" SET ""Activa"" = FALSE, ""Fecha_Limite"" = NOW() WHERE ""Id_Encuesta"" = @id";
                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // BITÁCORA: Edición (Desactivar)
                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
                    await Funciones.RegistrarBitacora(con, idUser, Modulo, Parametros.AccionesBitacora.Editar,
                        $"Desactivó encuesta ID: {id}", ip, null);
                }
                MostrarMensaje("Desactivada", "La encuesta ha sido desactivada.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Función para eliminar permanentemente una encuesta desde el panel de administración, solo si no tiene respuestas asociadas,
        /// </summary>
        /// <param name="id">Es un parámetro de tipo entero que representa el identificador único de la encuesta que se desea eliminar permanentemente desde el panel de administración</param>
        /// <returns>Retorna una acción que redirige al listado de encuestas después de intentar eliminar la encuesta especificada</returns>    
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Sin Permiso", "No tienes permisos para eliminar.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    string sqlCheck = @"SELECT COUNT(*) FROM ""Encuestas_Respuestas_Header"" WHERE ""Id_Encuesta"" = @id";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, con))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", id);
                        if ((long)await cmdCheck.ExecuteScalarAsync() > 0)
                        {
                            MostrarMensaje("No se puede eliminar", "Ya tiene respuestas. Solo puedes desactivarla.", TipoMensaje.Error);
                            return RedirectToAction("Index");
                        }
                    }

                    using (var trans = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            // LIMPIEZA EN CLOUDINARY DE TODA LA ENCUESTA
                            try
                            {
                                string folderPath = $"{sAmbiente}/Encuestas/{id}";

                                // 1. Borra imágenes (Por defecto Cloudinary asume ResourceType = Image)
                                await _cloudinary.DeleteResourcesByPrefixAsync($"{folderPath}/");

                                // 2. Borra documentos crudos (PDFs, DOCX) usando DeleteResourcesAsync con Prefix
                                var delRawParams = new DelResParams()
                                {
                                    Type = "upload",
                                    ResourceType = ResourceType.Raw,
                                    Prefix = $"{folderPath}/" 
                                };
                                await _cloudinary.DeleteResourcesAsync(delRawParams);

                                // 3. Borra la carpeta final (Cloudinary solo permite borrarla si ya está vacía)
                                await _cloudinary.DeleteFolderAsync(folderPath);
                            }
                            catch { /* Ignorar si falla la nube, la BD se limpia igual */ }

                            await new NpgsqlCommand($"DELETE FROM \"Encuestas_Preguntas\" WHERE \"Id_Encuesta\"={id}", con, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\"={id}", con, trans).ExecuteNonQueryAsync();

                            // BITÁCORA: Baja
                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();

                            await Funciones.RegistrarBitacora(con, idUser, Modulo, Parametros.AccionesBitacora.Borrar,
                                $"Eliminó encuesta ID: {id} permanentemente", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                MostrarMensaje("Eliminada", "Encuesta borrada correctamente.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Función para procesar y guardar las respuestas enviadas por el usuario final al completar una encuesta,
        /// </summary>
        /// <param name="form">Es un objeto de tipo IFormCollection que contiene los datos enviados por el usuario final al completar una encuesta,</param>
        /// <returns>Retorna una acción que redirige a la vista de agradecimiento después de procesar y guardar las respuestas de la encuesta</returns>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Finalizar(IFormCollection form)
        {
            // --------------------------------------------------------------------------------------
            // 1. VALIDACIONES PRELIMINARES DE ENTRADA (SANITIZACIÓN BÁSICA)
            // --------------------------------------------------------------------------------------
            if (!int.TryParse(form["IdEncuesta"], out int idEncuesta))
                return BadRequest("Solicitud incorrecta: Identificador de encuesta inválido.");

            string nombreExt = form["NombreExterno"];
            string emailExt = form["EmailExterno"];

            // Identificador de asistente si la encuesta proviene de un registro a evento
            int? idAsistenteEvento = null;
            if (int.TryParse(form["IdAsistenteEvento"], out int asisVal) && asisVal > 0)
                idAsistenteEvento = asisVal;

            string tokenAsistente = form["TokenAsistente"];
            string retorno = form["retorno"];

            // Capturar Iglesia Seleccionada si aplica
            int? idIglesiaSeleccionada = null;
            if (int.TryParse(form["IdIglesiaSeleccionada"], out int igId))
                idIglesiaSeleccionada = igId;

            // Validación Estricta de Email (Regex) si se proporcionó uno
            if (!string.IsNullOrWhiteSpace(emailExt))
            {
                var emailRegex = new System.Text.RegularExpressions.Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
                if (!emailRegex.IsMatch(emailExt))
                {
                    MostrarMensaje("Correo Inválido", "El formato del correo electrónico no es correcto.", TipoMensaje.Error);
                    return Redirect(Request.Headers["Referer"].ToString());
                }
            }

            // Identificar Usuario Logueado (si existe)
            int? idUsuario = null;
            if (User.Identity.IsAuthenticated)
            {
                var claimId = User.FindFirst("IdUsuario");
                if (claimId != null && int.TryParse(claimId.Value, out int uid)) idUsuario = uid;
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // ----------------------------------------------------------------------------------
                    // 2. CONTEXTO DE LA ENCUESTA (CARGA Y VALIDACIÓN DE ESTADO)
                    // ----------------------------------------------------------------------------------
                    var config = new { Activa = false, FechaLimite = (DateTime?)null, EsPrivada = false, IdGrupo = (int?)null, EsAnonima = false, ClaveUrl = "", SolicitarIglesia = false, Titulo = "" };

                    string sqlConf = @"SELECT ""Activa"", ""Fecha_Limite"", ""Es_Privada"", ""Id_Grupo_Acceso"", ""Es_Anonima"", ""Clave_Url"", ""Solicitar_Iglesia"", ""Titulo"" 
                               FROM ""Encuestas_Catalogo"" WHERE ""Id_Encuesta"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlConf, con))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncuesta);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                config = new
                                {
                                    Activa = (bool)r["Activa"],
                                    FechaLimite = r["Fecha_Limite"] as DateTime?,
                                    EsPrivada = (bool)r["Es_Privada"],
                                    IdGrupo = r["Id_Grupo_Acceso"] as int?,
                                    EsAnonima = (bool)r["Es_Anonima"],
                                    ClaveUrl = r["Clave_Url"].ToString(),
                                    SolicitarIglesia = r["Solicitar_Iglesia"] != DBNull.Value ? (bool)r["Solicitar_Iglesia"] : false,
                                    Titulo = r["Titulo"]?.ToString() ?? ""
                                };
                            }
                            else
                            {
                                MostrarMensaje("Error", "La encuesta solicitada no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index", "Home");
                            }
                        }
                    }

                    // Validaciones de Estado (Vencimiento / Desactivación)
                    if (!config.Activa)
                    {
                        MostrarMensaje("Encuesta No Disponible", "Esta encuesta ha sido desactivada por el administrador.", TipoMensaje.Info);
                        return RedirectToAction("Index", "Home");
                    }
                    if (config.FechaLimite.HasValue && config.FechaLimite.Value < DateTime.Now)
                    {
                        MostrarMensaje("Tiempo Finalizado", "El tiempo para responder esta encuesta ha terminado.", TipoMensaje.Info);
                        return RedirectToAction("Index", "Home");
                    }

                    // Validación del lado del servidor para la Iglesia
                    if (config.SolicitarIglesia && !idIglesiaSeleccionada.HasValue)
                    {
                        MostrarMensaje("Dato Requerido", "Debes seleccionar a qué iglesia perteneces.", TipoMensaje.Alerta);
                        return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                    }

                    // Si viene de un asistente de evento, recuperar su nombre y correo si no fueron enviados en el formulario
                    if (idAsistenteEvento.HasValue)
                    {
                        string sqlDatosAsis = @"SELECT b.""Nombre_Completo"", COALESCE(b.""Correo_Externo"", u.""Email"") AS ""Email_Asistente"", b.""Token_Pago_Externo"" 
                                               FROM ""Eventos_B_Asistentes"" b 
                                               JOIN ""Eventos_A_Registros"" r ON b.""Id_Registro"" = r.""Id_Registro"" 
                                               LEFT JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario"" = u.""Id_Usuario"" 
                                               WHERE b.""Id_Asistente"" = @asId LIMIT 1";
                        using (var cmdNom = new NpgsqlCommand(sqlDatosAsis, con))
                        {
                            cmdNom.Parameters.AddWithValue("@asId", idAsistenteEvento.Value);
                            using (var rNom = await cmdNom.ExecuteReaderAsync())
                            {
                                if (await rNom.ReadAsync())
                                {
                                    if (string.IsNullOrWhiteSpace(nombreExt)) nombreExt = rNom["Nombre_Completo"]?.ToString();
                                    if (string.IsNullOrWhiteSpace(emailExt)) emailExt = rNom["Email_Asistente"]?.ToString();
                                    if (string.IsNullOrWhiteSpace(tokenAsistente)) tokenAsistente = rNom["Token_Pago_Externo"]?.ToString();
                                }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(emailExt)) emailExt = "asistente@corban.com";
                        if (string.IsNullOrWhiteSpace(retorno) && !string.IsNullOrWhiteSpace(tokenAsistente)) retorno = tokenAsistente;
                    }

                    // ----------------------------------------------------------------------------------
                    // 3. VALIDACIÓN DE IDENTIDAD (ANONIMATO)
                    // ----------------------------------------------------------------------------------
                    // Si la encuesta NO es anónima y el usuario es externo (no logueado), Nombre y Email son OBLIGATORIOS.
                    if (!config.EsAnonima && !idUsuario.HasValue && !idAsistenteEvento.HasValue)
                    {
                        if (string.IsNullOrWhiteSpace(nombreExt) || string.IsNullOrWhiteSpace(emailExt))
                        {
                            MostrarMensaje("Datos Requeridos", "Esta encuesta no es anónima. Debes proporcionar tu Nombre y Correo para continuar.", TipoMensaje.Info);
                            return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                        }
                    }

                    // ----------------------------------------------------------------------------------
                    // 4. VALIDACIÓN DE PERMISOS (PRIVACIDAD / GRUPOS)
                    // ----------------------------------------------------------------------------------
                    if (config.EsPrivada && !idAsistenteEvento.HasValue)
                    {
                        if (!idUsuario.HasValue) return Unauthorized(); // Debe loguearse
                        if (config.IdGrupo.HasValue)
                        {
                            using (var cmd = new NpgsqlCommand("SELECT COUNT(1) FROM \"Sist_Grupos_Miembros\" WHERE \"Id_Grupo\"=@g AND \"Id_Usuario\"=@u", con))
                            {
                                cmd.Parameters.AddWithValue("@g", config.IdGrupo.Value);
                                cmd.Parameters.AddWithValue("@u", idUsuario.Value);
                                if ((long)await cmd.ExecuteScalarAsync() == 0)
                                {
                                    MostrarMensaje("Acceso Denegado", "No perteneces al grupo de usuarios autorizado para esta encuesta.", TipoMensaje.Error);
                                    return RedirectToAction("Index", "Home");
                                }
                            }
                        }
                    }

                    // ----------------------------------------------------------------------------------
                    // 5. VERIFICACIÓN DE DUPLICIDAD (PREVIA)
                    // ----------------------------------------------------------------------------------
                    bool checarDuplicado = false;
                    NpgsqlCommand cmdDup = new NpgsqlCommand();
                    cmdDup.Connection = con;
                    string sqlDuplicado = "";

                    // Modificado para recuperar Id_Respuesta en caso de que ya exista y poder mostrarle el folio de nuevo
                    if (idAsistenteEvento.HasValue)
                    {
                        sqlDuplicado = @"SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" 
                                         WHERE ""Id_Encuesta""=@id AND ""Id_Asistente_Evento""=@asId LIMIT 1";
                        cmdDup.Parameters.AddWithValue("@asId", idAsistenteEvento.Value);
                        checarDuplicado = true;
                    }
                    else if (idUsuario.HasValue)
                    {
                        sqlDuplicado = "SELECT \"Id_Respuesta\" FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Encuesta\"=@id AND \"Id_Usuario\"=@uid LIMIT 1";
                        cmdDup.Parameters.AddWithValue("@uid", idUsuario.Value);
                        checarDuplicado = true;
                    }
                    else if (!config.EsAnonima)
                    {
                        sqlDuplicado = @"SELECT ""Id_Respuesta"" FROM ""Encuestas_Respuestas_Header"" 
                                 WHERE ""Id_Encuesta""=@id 
                                 AND (LOWER(""Email_Externo"") = LOWER(@mail) OR LOWER(""Nombre_Externo"") = LOWER(@nom)) LIMIT 1";
                        cmdDup.Parameters.AddWithValue("@mail", emailExt ?? "");
                        cmdDup.Parameters.AddWithValue("@nom", nombreExt ?? "");
                        checarDuplicado = true;
                    }

                    if (checarDuplicado)
                    {
                        cmdDup.CommandText = sqlDuplicado;
                        cmdDup.Parameters.AddWithValue("@id", idEncuesta);
                        var objExiste = await cmdDup.ExecuteScalarAsync();
                        if (objExiste != null)
                        {
                            int idRespExistente = (int)objExiste;
                            ViewBag.Folio = idRespExistente.ToString("D6");
                            ViewBag.EsAnonima = config.EsAnonima;
                            ViewBag.TokenAsistente = tokenAsistente;
                            ViewBag.Retorno = retorno;
                            return View("EncuestaResuelta", new RedAJP.Models.EncuestaResponderViewModel 
                            { 
                                Titulo = !string.IsNullOrEmpty(config.Titulo) ? config.Titulo : config.ClaveUrl, 
                                NombreAsistente = nombreExt,
                                NombreUsuarioLogueado = nombreExt ?? "Usuario",
                                TokenAsistente = tokenAsistente,
                                Retorno = retorno
                            });
                        }
                    }

                    // ----------------------------------------------------------------------------------
                    // 6. CARGAR DEFINICIÓN COMPLETA DE PREGUNTAS (INCLUYENDO CONFIGURACIÓN)
                    // ----------------------------------------------------------------------------------
                    var preguntasDef = new List<dynamic>();
                    string sqlDef = @"SELECT p.""Id_Pregunta"", p.""Requerida"", p.""Texto"", p.""Id_Tipo"", p.""Configuracion"", t.""Tiene_Opciones""
                              FROM ""Encuestas_Preguntas"" p
                              INNER JOIN ""Encuestas_Tipos_Pregunta"" t ON p.""Id_Tipo"" = t.""Id_Tipo""
                              WHERE p.""Id_Encuesta"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlDef, con))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncuesta);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                preguntasDef.Add(new
                                {
                                    Id = (int)r["Id_Pregunta"],
                                    Requerida = (bool)r["Requerida"],
                                    Texto = r["Texto"].ToString(),
                                    Tipo = r["Id_Tipo"].ToString(),
                                    Config = r["Configuracion"]?.ToString() ?? "",
                                    TieneOpciones = (bool)r["Tiene_Opciones"]
                                });
                            }
                        }
                    }

                    if (preguntasDef.Count == 0)
                    {
                        MostrarMensaje("Error Crítico", "La encuesta no tiene preguntas configuradas.", TipoMensaje.Error);
                        return RedirectToAction("Index", "Home");
                    }

                    // ----------------------------------------------------------------------------------
                    // 7. PROCESAMIENTO Y VALIDACIÓN PROFUNDA DE RESPUESTAS
                    // ----------------------------------------------------------------------------------
                    var respuestasValidas = new Dictionary<int, string>();

                    foreach (var key in form.Keys)
                    {
                        if (key.StartsWith("R_") && int.TryParse(key.Substring(2), out int idPregunta))
                        {
                            var def = preguntasDef.FirstOrDefault(p => p.Id == idPregunta);
                            if (def == null) continue;

                            string respuestaRaw = form[key];
                            string valorLimpio = "";
                            string tipo = def.Tipo;
                            string configStr = def.Config;

                            // A. VALIDACIÓN: OPCIÓN ÚNICA Y SI/NO
                            if (tipo == "Opcion" || tipo == "SiNo")
                            {
                                string val = respuestaRaw.Trim();
                                if (string.IsNullOrEmpty(val)) continue;

                                var opcionesValidas = tipo == "SiNo"
                                                    ? new List<string> { "Sí", "No" }
                                                    : configStr.Split('|').Select(x => x.Trim()).ToList();

                                if (!opcionesValidas.Contains(val))
                                {
                                    MostrarMensaje("Valor no permitido", $"La opción '{val}' no es válida para la pregunta '{def.Texto}'.", TipoMensaje.Error);
                                    return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                                }
                                valorLimpio = val;
                            }

                            // B. VALIDACIÓN: CASILLAS
                            else if (tipo == "Casillas")
                            {
                                var selecciones = respuestaRaw.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
                                if (!selecciones.Any()) continue;

                                var opcionesValidas = configStr.Split('|').Select(x => x.Trim()).ToList();

                                foreach (var sel in selecciones)
                                {
                                    if (!opcionesValidas.Contains(sel))
                                    {
                                        MostrarMensaje("Selección inválida", $"La opción '{sel}' no existe en la pregunta '{def.Texto}'.", TipoMensaje.Error);
                                        return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                                    }
                                }
                                valorLimpio = string.Join("|", selecciones);
                            }

                            // C. VALIDACIÓN: RANGOS NUMÉRICOS
                            else if (tipo == "RangoNumerico" || tipo.StartsWith("Rating"))
                            {
                                if (string.IsNullOrEmpty(respuestaRaw)) continue;

                                if (!double.TryParse(respuestaRaw, out double valNum))
                                {
                                    MostrarMensaje("Error de formato", $"La pregunta '{def.Texto}' requiere un valor numérico.", TipoMensaje.Error);
                                    return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                                }

                                double min = 1, max = 5;

                                if (tipo == "RangoNumerico")
                                {
                                    var parts = configStr.Split('|');
                                    if (parts.Length >= 2)
                                    {
                                        double.TryParse(parts[0], out min);
                                        double.TryParse(parts[1], out max);
                                    }
                                    else { min = 0; max = 10; }
                                }

                                if (valNum < min || valNum > max)
                                {
                                    MostrarMensaje("Valor fuera de rango", $"Para '{def.Texto}', el valor debe estar entre {min} y {max}.", TipoMensaje.Error);
                                    return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                                }
                                valorLimpio = respuestaRaw;
                            }

                            // D. VALIDACIÓN: TEXTO / FECHA
                            else
                            {
                                valorLimpio = respuestaRaw.Trim();
                                if (valorLimpio.Length > 2000) valorLimpio = valorLimpio.Substring(0, 2000);
                            }

                            if (!string.IsNullOrEmpty(valorLimpio)) respuestasValidas.Add(idPregunta, valorLimpio);
                        }
                    }

                    // ----------------------------------------------------------------------------------
                    // 8. VALIDACIÓN FINAL: CAMPOS OBLIGATORIOS
                    // ----------------------------------------------------------------------------------
                    foreach (var p in preguntasDef)
                    {
                        if (p.Requerida && !respuestasValidas.ContainsKey(p.Id))
                        {
                            MostrarMensaje("Pregunta Incompleta", $"Es obligatorio responder: '{p.Texto}'", TipoMensaje.Info);
                            return RedirectToAction("Responder", new { clave = config.ClaveUrl });
                        }
                    }

                    // ----------------------------------------------------------------------------------
                    // 9. TRANSACCIÓN DE GUARDADO (COMMIT)
                    // ----------------------------------------------------------------------------------
                    int idRespuestaGenerada = 0;

                    using (var trans = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            if (checarDuplicado)
                            {
                                cmdDup.Transaction = trans;
                                var objExiste = await cmdDup.ExecuteScalarAsync();
                                if (objExiste != null) throw new Exception("Respuesta duplicada (Concurrencia).");
                            }

                            // A. Insertar Encabezado
                            string sqlH = @"INSERT INTO ""Encuestas_Respuestas_Header"" 
                                  (""Id_Encuesta"", ""Fecha"", ""Id_Usuario"", ""Nombre_Externo"", ""Email_Externo"", ""Id_Iglesia_Seleccionada"", ""Id_Asistente_Evento"") 
                                  VALUES (@id, NOW(), @uid, @nom, @mail, @idig, @asId) RETURNING ""Id_Respuesta""";

                            using (var cmd = new NpgsqlCommand(sqlH, con, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", idEncuesta);
                                cmd.Parameters.AddWithValue("@uid", (object)idUsuario ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@nom", (object)nombreExt ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@mail", (object)emailExt ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@idig", (object)idIglesiaSeleccionada ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@asId", (object)idAsistenteEvento ?? DBNull.Value); // NUEVO
                                idRespuestaGenerada = (int)await cmd.ExecuteScalarAsync();
                            }

                            // B. Insertar Detalles
                            string sqlD = @"INSERT INTO ""Encuestas_Respuestas_Detalle"" 
                                  (""Id_Respuesta"", ""Id_Pregunta"", ""Valor_Respuesta"") 
                                  VALUES (@ir, @ip, @val)";

                            foreach (var resp in respuestasValidas)
                            {
                                using (var cmd = new NpgsqlCommand(sqlD, con, trans))
                                {
                                    cmd.Parameters.AddWithValue("@ir", idRespuestaGenerada);
                                    cmd.Parameters.AddWithValue("@ip", resp.Key);
                                    cmd.Parameters.AddWithValue("@val", resp.Value);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            // C. PROCESAMIENTO DE ARCHIVOS CON CLOUDINARY
                            if (Request.Form.Files.Count > 0)
                            {
                                long limiteBytes = 2 * 1024 * 1024;

                                foreach (var file in Request.Form.Files)
                                {
                                    if (file.Name.StartsWith("File_") && int.TryParse(file.Name.Substring(5), out int idPregunta))
                                    {
                                        var def = preguntasDef.FirstOrDefault(p => p.Id == idPregunta);
                                        if (def == null) continue;
                                        if (def.Tipo != "Archivo") continue;

                                        if (file.Length > limiteBytes)
                                            throw new Exception($"El archivo para '{def.Texto}' excede el límite de 2 MB.");

                                        string ext = Path.GetExtension(file.FileName).ToLower();
                                        var permitidas = ((string)def.Config).ToLower().Split('|');

                                        if (permitidas.Any() && !permitidas.Contains(ext))
                                            throw new Exception($"El formato '{ext}' no está permitido en la pregunta '{def.Texto}'.");

                                        string urlCloudinary = await SubirArchivoCloudinary(file, $"{sAmbiente}/Encuestas/{idEncuesta}");

                                        string sqlArc = @"INSERT INTO ""Encuestas_Archivos"" 
                                              (""Id_Respuesta"", ""Id_Pregunta"", ""Nombre_Original"", ""Nombre_Sistema"", 
                                               ""Extension"", ""Tamano_Bytes"", ""Ruta_Fisica"")
                                              VALUES (@ir, @ip, @nomOrg, @nomSis, @ext, @tam, @rut)";

                                        using (var cmdArc = new NpgsqlCommand(sqlArc, con, trans))
                                        {
                                            cmdArc.Parameters.AddWithValue("@ir", idRespuestaGenerada);
                                            cmdArc.Parameters.AddWithValue("@ip", idPregunta);
                                            cmdArc.Parameters.AddWithValue("@nomOrg", file.FileName);
                                            cmdArc.Parameters.AddWithValue("@nomSis", "Cloudinary");
                                            cmdArc.Parameters.AddWithValue("@ext", ext);
                                            cmdArc.Parameters.AddWithValue("@tam", file.Length);
                                            cmdArc.Parameters.AddWithValue("@rut", urlCloudinary);
                                            await cmdArc.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // D. VERIFICAR OBLIGATORIEDAD DE ARCHIVOS
                            foreach (var p in preguntasDef)
                            {
                                if (p.Tipo == "Archivo" && p.Requerida)
                                {
                                    var checkCmd = new NpgsqlCommand("SELECT COUNT(1) FROM \"Encuestas_Archivos\" WHERE \"Id_Respuesta\"=@ir AND \"Id_Pregunta\"=@ip", con, trans);
                                    checkCmd.Parameters.AddWithValue("@ir", idRespuestaGenerada);
                                    checkCmd.Parameters.AddWithValue("@ip", (int)p.Id);
                                    if ((long)await checkCmd.ExecuteScalarAsync() == 0)
                                    {
                                        throw new Exception($"Debes subir un archivo para: {p.Texto}");
                                    }
                                }
                            }

                            // C. Bitácora 
                            if (idUsuario.HasValue)
                            {
                                string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
                                await Funciones.RegistrarBitacora(con, idUsuario.Value, Modulo,
                                    Parametros.AccionesBitacora.Crear,
                                    $"Respondió encuesta {idEncuesta} (Respuesta #{idRespuestaGenerada})",
                                    ip, trans);
                            }

                            await trans.CommitAsync();
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            if (ex.Message.Contains("duplicada") || ex.Message.Contains("unique"))
                                return View("EncuestaResuelta");

                            throw;
                        }
                    }

                    // Todo salió bien, pasamos las propiedades a la vista de éxito
                    ViewBag.Folio = idRespuestaGenerada.ToString("D6");
                    ViewBag.EsAnonima = config.EsAnonima;
                    ViewBag.TokenAsistente = tokenAsistente;
                    ViewBag.IdAsistenteEvento = idAsistenteEvento;
                    ViewBag.Retorno = retorno;
                    
                    if (form.ContainsKey("evt") && !string.IsNullOrEmpty(form["evt"]))
                    {
                        ViewBag.IdEventoRetorno = form["evt"].ToString();
                    }

                    return View("Agradecimiento");
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Sistema", "Ocurrió un problema al procesar tu respuesta: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }
        }

        /// <summary>
        /// Genera y descarga un archivo ZIP conteniendo todos los archivos adjuntos de la encuesta,
        /// renombrándolos dinámicamente para facilitar su cruce con el reporte de Excel.
        /// </summary>
        public async Task<IActionResult> DescargarTodoZip(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return Forbid();

            try
            {
                // 1. Obtener la lista de archivos con sus metadatos
                var archivosParaZip = new List<dynamic>();
                string tituloEncuesta = "Encuesta";

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // Obtener título para el nombre del ZIP
                    using (var cmdT = new NpgsqlCommand("SELECT \"Titulo\" FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\" = @id", con))
                    {
                        cmdT.Parameters.AddWithValue("@id", id);
                        tituloEncuesta = (string)await cmdT.ExecuteScalarAsync();
                    }

                    // Query optimizada: Trae rutas (URLs de Cloudinary) y IDs clave
                    string sql = @"
                SELECT a.""Ruta_Fisica"", a.""Nombre_Original"", a.""Id_Respuesta"", a.""Id_Pregunta"", 
                       p.""Orden""
                FROM ""Encuestas_Archivos"" a
                INNER JOIN ""Encuestas_Respuestas_Header"" h ON a.""Id_Respuesta"" = h.""Id_Respuesta""
                INNER JOIN ""Encuestas_Preguntas"" p ON a.""Id_Pregunta"" = p.""Id_Pregunta""
                WHERE h.""Id_Encuesta"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                archivosParaZip.Add(new
                                {
                                    Ruta = r["Ruta_Fisica"].ToString(),
                                    NombreOrg = r["Nombre_Original"].ToString(),
                                    IdRespuesta = (int)r["Id_Respuesta"],
                                    Orden = (int)r["Orden"]
                                });
                            }
                        }
                    }
                }

                if (archivosParaZip.Count == 0)
                {
                    MostrarMensaje("Sin Archivos", "No hay archivos cargados en esta encuesta para descargar.", TipoMensaje.Info);
                    return RedirectToAction("Resultados", new { id = id });
                }

                // 2. Crear el ZIP en memoria descargando los archivos desde Cloudinary
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        using (var httpClient = new System.Net.Http.HttpClient())
                        {
                            foreach (var item in archivosParaZip)
                            {
                                string rutaUrl = item.Ruta;

                                // Validamos que sea una URL de Cloudinary (o cualquier http)
                                if (!string.IsNullOrEmpty(rutaUrl) && rutaUrl.StartsWith("http"))
                                {
                                    // ESTRATEGIA DE RENOMBRADO:
                                    // Formato: R[IdRespuesta]_P[Orden]_[NombreOriginal]
                                    // Ejemplo: R105_P3_FotoEvidencia.jpg
                                    string extension = Path.GetExtension(item.NombreOrg);
                                    string nombreLimpio = Path.GetFileNameWithoutExtension(item.NombreOrg);

                                    // Limpiamos caracteres inválidos del nombre original por si acaso
                                    foreach (char c in Path.GetInvalidFileNameChars())
                                    {
                                        nombreLimpio = nombreLimpio.Replace(c, '_');
                                    }

                                    // Construcción del nombre final para el ZIP
                                    string nombreEnZip = $"R{item.IdRespuesta}_P{item.Orden}_{nombreLimpio}{extension}";

                                    // Crear entrada en el ZIP
                                    // Usamos CompressionLevel.Fastest para no saturar el CPU del servidor
                                    var entry = archive.CreateEntry(nombreEnZip, CompressionLevel.Fastest);

                                    try
                                    {
                                        using (var entryStream = entry.Open())
                                        using (var streamFromUrl = await httpClient.GetStreamAsync(rutaUrl))
                                        {
                                            await streamFromUrl.CopyToAsync(entryStream);
                                        }
                                    }
                                    catch
                                    {
                                        /* Si un archivo falla en descargarse desde la nube (ej. fue borrado), 
                                           el ZIP sigue construyéndose con el resto de los archivos. */
                                    }
                                }
                            }
                        }
                    }

                    // 3. Devolver el archivo ZIP al usuario
                    memoryStream.Position = 0;
                    string nombreZip = $"Adjuntos_{tituloEncuesta.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd}.zip";
                    return File(memoryStream.ToArray(), "application/zip", nombreZip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo generar el ZIP: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Resultados", new { id = id });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DescargarArchivo(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return Forbid();

            try
            {
                string rutaFisica = "";
                string nombreDescarga = "";

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Nombre_Original"", ""Ruta_Fisica"" FROM ""Encuestas_Archivos"" WHERE ""Id_Archivo"" = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                nombreDescarga = r["Nombre_Original"].ToString();
                                rutaFisica = r["Ruta_Fisica"].ToString();
                            }
                            else return NotFound();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(rutaFisica) && rutaFisica.StartsWith("http"))
                {
                    using (var httpClient = new System.Net.Http.HttpClient())
                    {
                        var fileBytes = await httpClient.GetByteArrayAsync(rutaFisica);
                        return File(fileBytes, "application/octet-stream", nombreDescarga);
                    }
                }
                else
                {
                    return NotFound("El archivo no se encuentra o la ruta es inválida.");
                }
            }
            catch (Exception ex)
            {
                return BadRequest("Error al descargar: " + ex.Message);
            }
        }

        /// <summary>
        /// Función para mostrar los resultados estadísticos de una encuesta específica en el panel de administración,
        /// </summary>
        public async Task<IActionResult> Resultados(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
                return RedirectToAction("Index");

            var modelo = new EncuestaResultadosViewModel { Id = id };
            var respuestasPlanas = new List<dynamic>();
            var preguntasMap = new List<dynamic>();

            var archivosCargados = new List<ArchivoDetalleView>();
            var mapaArchivoPregunta = new Dictionary<int, int>();

            bool esAnonima = false;

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // A. Header (Título y Clave)
                    using (var cmd = new NpgsqlCommand("SELECT \"Titulo\", \"Clave_Url\", \"Es_Anonima\" FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\"=@id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.ClaveUrl = r["Clave_Url"].ToString();
                                esAnonima = (bool)r["Es_Anonima"];
                            }
                            else
                            {
                                MostrarMensaje("Error", "La encuesta solicitada no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index", "Home");
                            }
                        }
                    }

                    ViewBag.EsAnonima = esAnonima;

                    // B. CALCULAR ESPACIO TOTAL
                    string sqlSpace = @"SELECT COALESCE(SUM(a.""Tamano_Bytes""), 0) 
                                FROM ""Encuestas_Archivos"" a
                                INNER JOIN ""Encuestas_Respuestas_Header"" h ON a.""Id_Respuesta"" = h.""Id_Respuesta""
                                WHERE h.""Id_Encuesta"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlSpace, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        long bytes = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        modelo.EspacioTotalOcupadoMB = bytes / 1024.0 / 1024.0;
                    }

                    // C. PREGUNTAS
                    string sqlP = @"SELECT ""Id_Pregunta"", ""Texto"", ""Id_Tipo"", ""Orden"", ""Configuracion"" 
                            FROM ""Encuestas_Preguntas"" 
                            WHERE ""Id_Encuesta""=@id 
                            ORDER BY ""Orden"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlP, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                preguntasMap.Add(new
                                {
                                    Id = (int)r["Id_Pregunta"],
                                    Texto = r["Texto"].ToString(),
                                    Tipo = r["Id_Tipo"].ToString(),
                                    Orden = (int)r["Orden"],
                                    Configuracion = r["Configuracion"]?.ToString() ?? ""
                                });
                            }
                        }
                    }

                    // D. CARGAR METADATA DE ARCHIVOS
                    string sqlArc = @"
                SELECT a.*, 
                       CASE WHEN h.""Id_Usuario"" > 0 THEN u.""NombreCompleto"" ELSE COALESCE(h.""Nombre_Externo"", 'Anónimo') END as ""Autor"",
                       h.""Fecha""
                FROM ""Encuestas_Archivos"" a
                INNER JOIN ""Encuestas_Respuestas_Header"" h ON a.""Id_Respuesta"" = h.""Id_Respuesta""
                LEFT JOIN ""Sist_Usuarios"" u ON h.""Id_Usuario"" = u.""Id_Usuario""
                WHERE h.""Id_Encuesta"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlArc, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idArchivo = (int)r["Id_Archivo"];
                                int idPregunta = (int)r["Id_Pregunta"];

                                if (!mapaArchivoPregunta.ContainsKey(idArchivo))
                                    mapaArchivoPregunta.Add(idArchivo, idPregunta);

                                archivosCargados.Add(new ArchivoDetalleView
                                {
                                    IdRespuesta = (int)r["Id_Respuesta"],
                                    IdArchivo = idArchivo,
                                    NombreOriginal = r["Nombre_Original"].ToString(),
                                    Extension = r["Extension"].ToString(),
                                    TamanoBytes = (long)r["Tamano_Bytes"],
                                    Autor = esAnonima ? $"Participante Anónimo (#{r["Id_Respuesta"]})" : r["Autor"].ToString(),
                                    Fecha = (DateTime)r["Fecha"],
                                    UrlDescarga = Url.Action("DescargarArchivo", new { id = idArchivo })
                                });
                            }
                        }
                    }

                    // E. RESPUESTAS DE TEXTO Y PARTICIPANTES
                    string sqlD = @"
                SELECT d.""Id_Detalle"", d.""Id_Pregunta"", d.""Valor_Respuesta"", 
                       h.""Id_Respuesta"", h.""Fecha"", h.""Id_Usuario"",
                       CASE WHEN h.""Id_Usuario"" > 0 THEN u.""NombreCompleto"" ELSE COALESCE(h.""Nombre_Externo"", 'Anónimo') END as ""Autor"",
                       CASE WHEN h.""Id_Usuario"" > 0 THEN u.""Email"" ELSE COALESCE(h.""Email_Externo"", '') END as ""Email"",
                       ig.nombre as ""Nombre_Iglesia"", m.nombre as ""Municipio"", ig.localidad as ""Localidad""
                FROM ""Encuestas_Respuestas_Header"" h
                LEFT JOIN ""Encuestas_Respuestas_Detalle"" d ON h.""Id_Respuesta"" = d.""Id_Respuesta""
                LEFT JOIN ""Sist_Usuarios"" u ON h.""Id_Usuario"" = u.""Id_Usuario""
                LEFT JOIN iciar_iglesias ig ON h.""Id_Iglesia_Seleccionada"" = ig.id
                LEFT JOIN iciar_municipios m ON ig.municipio_id = m.id
                WHERE h.""Id_Encuesta"" = @id
                ORDER BY h.""Fecha"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlD, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                if (r["Valor_Respuesta"] != DBNull.Value)
                                {
                                    respuestasPlanas.Add(new
                                    {
                                        IdDetalle = (int)r["Id_Detalle"],
                                        IdPregunta = (int)r["Id_Pregunta"],
                                        IdRespuesta = (int)r["Id_Respuesta"],
                                        Valor = r["Valor_Respuesta"].ToString(),
                                        Fecha = (DateTime)r["Fecha"],
                                        Autor = esAnonima ? $"Anónimo #{r["Id_Respuesta"]}" : r["Autor"].ToString()
                                    });
                                }

                                int idR = (int)r["Id_Respuesta"];
                                if (!modelo.ListaParticipantes.Any(x => x.IdRespuesta == idR))
                                {
                                    // Procesamos la iglesia con localidad
                                    string nIglesia = r["Nombre_Iglesia"] != DBNull.Value ? r["Nombre_Iglesia"].ToString() : "";
                                    string nMunicipio = r["Municipio"] != DBNull.Value ? r["Municipio"].ToString() : "";
                                    string nLocalidad = r["Localidad"] != DBNull.Value ? r["Localidad"].ToString() : "";

                                    string txtLoc = !string.IsNullOrEmpty(nLocalidad) ? $", {nLocalidad}" : "";
                                    string iglesiaAsignada = !string.IsNullOrEmpty(nIglesia) ? $"{nIglesia} - {nMunicipio}{txtLoc}" : "No especificada";

                                    modelo.ListaParticipantes.Add(new RespuestaHeaderView
                                    {
                                        IdRespuesta = idR,
                                        Fecha = (DateTime)r["Fecha"],
                                        Autor = esAnonima ? $"Participante Anónimo (#{idR})" : r["Autor"].ToString(),
                                        Email = esAnonima ? "Oculto por privacidad" : r["Email"].ToString(),
                                        EsInterno = (r["Id_Usuario"] as int? ?? 0) > 0,
                                        Iglesia = iglesiaAsignada
                                    });
                                }
                            }
                        }
                    }
                }

                // F. PROCESAMIENTO ESTADÍSTICO
                modelo.TotalRespuestas = modelo.ListaParticipantes.Count;

                foreach (var p in preguntasMap)
                {
                    var item = new PreguntaResultadoView
                    {
                        Texto = p.Texto,
                        Tipo = p.Tipo,
                        Orden = p.Orden,
                        Configuracion = p.Configuracion
                    };

                    if (p.Tipo == "Archivo")
                    {
                        int idPreguntaActual = (int)p.Id;
                        item.ArchivosRecibidos = archivosCargados
                            .Where(a => mapaArchivoPregunta.ContainsKey(a.IdArchivo) && mapaArchivoPregunta[a.IdArchivo] == idPreguntaActual)
                            .OrderByDescending(a => a.Fecha)
                            .ToList();
                    }

                    var respuestasItem = respuestasPlanas.Where(x => x.IdPregunta == p.Id).ToList();

                    if (!respuestasItem.Any())
                    {
                        modelo.Preguntas.Add(item);
                        continue;
                    }

                    if (p.Tipo == "Texto" || p.Tipo == "Parrafo" || p.Tipo == "Fecha")
                    {
                        item.UltimasRespuestas = respuestasItem.Take(30).Select(x => new RespuestaTextoDetalle
                        {
                            IdDetalle = x.IdDetalle,
                            Texto = x.Valor,
                            Autor = x.Autor,
                            Fecha = x.Fecha
                        }).ToList();
                    }
                    else if (p.Tipo == "RatingStar" || p.Tipo == "RatingEmoji" || p.Tipo == "RangoNumerico")
                    {
                        double suma = 0; int count = 0;
                        foreach (var r in respuestasItem)
                        {
                            if (double.TryParse(r.Valor, out double d)) { suma += d; count++; }
                        }
                        if (count > 0) item.Promedio = suma / count;
                    }
                    else
                    {
                        var conteo = new Dictionary<string, int>();
                        foreach (var r in respuestasItem)
                        {
                            var opciones = (p.Tipo == "Casillas") ? r.Valor.Split('|') : new string[] { r.Valor };
                            foreach (var op in opciones)
                            {
                                if (string.IsNullOrWhiteSpace(op)) continue;
                                var k = op.Trim();
                                if (!conteo.ContainsKey(k)) conteo[k] = 0;
                                conteo[k]++;
                            }
                        }
                        int total = conteo.Values.Sum();
                        foreach (var kv in conteo)
                        {
                            item.ConteoOpciones.Add(new DatoGraficaEncuesta
                            {
                                Etiqueta = kv.Key,
                                Cantidad = kv.Value,
                                Porcentaje = total > 0 ? (kv.Value * 100.0 / total) : 0
                            });
                        }
                        item.ConteoOpciones = item.ConteoOpciones.OrderByDescending(x => x.Cantidad).ToList();
                    }
                    modelo.Preguntas.Add(item);
                }

                foreach (var part in modelo.ListaParticipantes)
                {
                    var textos = respuestasPlanas.Where(x => x.IdRespuesta == part.IdRespuesta).Take(3);
                    foreach (var t in textos)
                    {
                        string pregTexto = "P";
                        foreach (var pInfo in preguntasMap)
                        {
                            if (pInfo.Id == t.IdPregunta) { pregTexto = pInfo.Texto; break; }
                        }
                        string valCorto = t.Valor.Length > 30 ? t.Valor.Substring(0, 30) + "..." : t.Valor;
                        part.PreviewRespuestas.Add($"<strong>{pregTexto}:</strong> {valCorto}");
                    }

                    var archivosPart = archivosCargados.Where(x => x.IdRespuesta == part.IdRespuesta);
                    foreach (var arc in archivosPart)
                    {
                        part.PreviewRespuestas.Add($"<i class='fa-solid fa-paperclip'></i> {arc.NombreOriginal}");
                    }

                    if (part.PreviewRespuestas.Count == 0) part.PreviewRespuestas.Add("Sin respuestas visibles.");
                }

                return View(modelo);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudieron cargar los resultados: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }
        }

        /// <summary>
        /// Elimina un archivo específico del servidor y de la base de datos para liberar espacio,
        /// sin eliminar el resto de la respuesta del usuario.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarArchivoIndividual(int idArchivo, int idEncuesta)
        {
            // 1. Validar Permisos
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permisos para eliminar archivos.", TipoMensaje.Error);
                return RedirectToAction("Resultados", new { id = idEncuesta });
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // 2. Obtener ruta física antes de borrar registro
                    string rutaFisica = "";
                    string nombreOriginal = "";
                    using (var cmd = new NpgsqlCommand("SELECT \"Ruta_Fisica\", \"Nombre_Original\" FROM \"Encuestas_Archivos\" WHERE \"Id_Archivo\" = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", idArchivo);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                rutaFisica = r["Ruta_Fisica"].ToString();
                                nombreOriginal = r["Nombre_Original"].ToString();
                            }
                            else
                            {
                                MostrarMensaje("Error", "El archivo no existe.", TipoMensaje.Error);
                                return RedirectToAction("Resultados", new { id = idEncuesta });
                            }
                        }
                    }

                    // 3. Borrar de Cloudinary
                    if (!string.IsNullOrEmpty(rutaFisica) && rutaFisica.Contains("cloudinary.com"))
                    {
                        try
                        {
                            var uri = new Uri(rutaFisica);
                            var segments = uri.Segments;
                            int uploadIndex = Array.IndexOf(segments, "upload/");
                            if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                            {
                                // Extraemos toda la ruta final (ej. dev/Encuestas/1/archivo.pdf)
                                string publicIdExt = string.Join("", segments.Skip(uploadIndex + 2)).Replace("%20", " ").Trim('/');

                                string ext = Path.GetExtension(rutaFisica).ToLower();
                                var resType = (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp") ? ResourceType.Image : ResourceType.Raw;

                                // Si es imagen, Cloudinary requiere el ID SIN extensión.
                                // Si es Raw (PDF, etc), Cloudinary requiere el ID CON extensión.
                                string finalPublicId = (resType == ResourceType.Image)
                                                        ? Path.ChangeExtension(publicIdExt, null)
                                                        : publicIdExt;

                                await _cloudinary.DestroyAsync(new DeletionParams(finalPublicId) { ResourceType = resType });
                            }
                        }
                        catch { /* Ignorar error de nube y limpiar DB */ }
                    }

                    // 4. Borrar Registro de Base de Datos
                    using (var cmdDel = new NpgsqlCommand("DELETE FROM \"Encuestas_Archivos\" WHERE \"Id_Archivo\" = @id", con))
                    {
                        cmdDel.Parameters.AddWithValue("@id", idArchivo);
                        await cmdDel.ExecuteNonQueryAsync();
                    }

                    // 5. Bitácora
                    int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                    await Funciones.RegistrarBitacora(con, idUser, Modulo, Parametros.AccionesBitacora.Borrar,
                        $"Eliminó archivo '{nombreOriginal}' (ID {idArchivo}) de encuesta {idEncuesta}",
                        HttpContext.Connection.RemoteIpAddress?.ToString(), null);
                }

                MostrarMensaje("Espacio Liberado", "El archivo ha sido eliminado correctamente.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo eliminar el archivo: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Resultados", new { id = idEncuesta });
        }
        /// <summary>
        /// Elimina una respuesta completa (header y detalles) de una encuesta específica,
        /// </summary>
        /// <param name="idRespuesta">Es un parámetro de tipo entero que representa el identificador único de la respuesta que se desea eliminar de la encuesta</param>
        /// <param name="idEncuesta">Es un parámetro de tipo entero que representa el identificador único de la encuesta de la cual se desea eliminar una respuesta específica</param>
        /// <returns>Retorna una acción que redirige a la vista de resultados de la encuesta después de eliminar la respuesta especificada</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarRespuestaCompleta(int idRespuesta, int idEncuesta)
        {
            // 1. Validar Permisos de Edición/Borrado
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                 MostrarMensaje("Error", "No tienes permisos", TipoMensaje.Error);
                return RedirectToAction("Resultados", new { id = idEncuesta });
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // Usamos transacción: O se borra todo, o no se borra nada
                    using (var trans = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            // PASO PREVIO: Recuperar y borrar archivos en Cloudinary
                            string sqlGetFiles = "SELECT \"Ruta_Fisica\" FROM \"Encuestas_Archivos\" WHERE \"Id_Respuesta\" = @id";
                            using (var cmdF = new NpgsqlCommand(sqlGetFiles, con, trans))
                            {
                                cmdF.Parameters.AddWithValue("@id", idRespuesta);
                                using (var rArc = await cmdF.ExecuteReaderAsync())
                                {
                                    while (await rArc.ReadAsync())
                                    {
                                        string path = rArc["Ruta_Fisica"]?.ToString();
                                        if (!string.IsNullOrEmpty(path) && path.Contains("cloudinary.com"))
                                        {
                                            try
                                            {
                                                var uri = new Uri(path);
                                                var segments = uri.Segments;
                                                int uploadIndex = Array.IndexOf(segments, "upload/");
                                                if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                                                {
                                                    string publicIdExt = string.Join("", segments.Skip(uploadIndex + 2)).Replace("%20", " ").Trim('/');

                                                    string ext = Path.GetExtension(path).ToLower();
                                                    var resType = (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp") ? ResourceType.Image : ResourceType.Raw;

                                                    // LÓGICA CORREGIDA:
                                                    string finalPublicId = (resType == ResourceType.Image)
                                                                            ? Path.ChangeExtension(publicIdExt, null)
                                                                            : publicIdExt;

                                                    await _cloudinary.DestroyAsync(new DeletionParams(finalPublicId) { ResourceType = resType });
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }

                            // PASO A: Borrar los detalles (hijos)
                            string sqlDetalle = "DELETE FROM \"Encuestas_Respuestas_Detalle\" WHERE \"Id_Respuesta\" = @id";
                            using (var cmd = new NpgsqlCommand(sqlDetalle, con, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", idRespuesta);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // PASO B: Borrar el encabezado (padre)
                            string sqlHeader = "DELETE FROM \"Encuestas_Respuestas_Header\" WHERE \"Id_Respuesta\" = @id";
                            using (var cmd = new NpgsqlCommand(sqlHeader, con, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", idRespuesta);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // PASO C: Registrar en Bitácora (Auditoría)
                            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                            await Funciones.RegistrarBitacora(con, idUser, Modulo,
                                Parametros.AccionesBitacora.Borrar,
                                $"Eliminó registro completo ID: {idRespuesta} de Encuesta {idEncuesta}",
                                HttpContext.Connection.RemoteIpAddress?.ToString(),
                                trans);

                            // Confirmar cambios
                            await trans.CommitAsync();
                        }
                        catch
                        {
                            // Si algo falla, deshacemos todo para no dejar basura
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
                MostrarMensaje("Éxito", "Registro eliminado correctamente.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            // Regresamos al dashboard para ver la tabla actualizada
            return RedirectToAction("Resultados", new { id = idEncuesta });
        }

        /// <summary>
        /// Elimina un detalle específico de respuesta de una encuesta,
        /// </summary>
        /// <param name="idDetalle">Es un parámetro de tipo entero que representa el identificador único del detalle de respuesta que se desea eliminar de la encuesta</param>
        /// <param name="idEncuesta">Es un parámetro de tipo entero que representa el identificador único de la encuesta de la cual se desea eliminar un detalle específico de respuesta</param> 
        /// <returns>Retorna una acción que redirige a la vista de resultados de la encuesta después de eliminar el detalle de respuesta especificado</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarRespuestaDetalle(int idDetalle, int idEncuesta)
        {
            // Solo permitimos borrar si tiene permiso de EDICIÓN o BORRADO
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Sin permisos", "No tienes permiso para moderar respuestas.", TipoMensaje.Error);
                return RedirectToAction("Resultados", new { id = idEncuesta });
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();
                    // Borrado simple por ID
                    string sql = "DELETE FROM \"Encuestas_Respuestas_Detalle\" WHERE \"Id_Detalle\" = @id";
                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@id", idDetalle);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Bitácora
                    int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                    await Funciones.RegistrarBitacora(con, idUser, Modulo, Parametros.AccionesBitacora.Borrar,
                        $"Moderación: Eliminó respuesta texto ID {idDetalle}", HttpContext.Connection.RemoteIpAddress?.ToString(), null);
                }
                MostrarMensaje("Eliminado", "Comentario eliminado correctamente.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo eliminar: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Resultados", new { id = idEncuesta });
        }

        /// <summary>
        /// Exporta los resultados de una encuesta específica a un archivo Excel descargable,
        /// </summary>
        public async Task<IActionResult> ExportarExcel(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // A. Título y Anonimato
                    string tituloEncuesta = "";
                    bool esAnonima = false;
                    using (var cmd = new NpgsqlCommand("SELECT \"Titulo\", \"Es_Anonima\" FROM \"Encuestas_Catalogo\" WHERE \"Id_Encuesta\" = @id", con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                tituloEncuesta = r["Titulo"].ToString();
                                esAnonima = (bool)r["Es_Anonima"];
                            }
                            else return NotFound();
                        }
                    }

                    // B. Columnas
                    var columnasPreguntas = new List<dynamic>();
                    string sqlP = @"SELECT ""Id_Pregunta"", ""Texto"" FROM ""Encuestas_Preguntas"" WHERE ""Id_Encuesta""=@id ORDER BY ""Orden"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlP, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync()) columnasPreguntas.Add(new { Id = (int)r["Id_Pregunta"], Texto = r["Texto"].ToString() });
                        }
                    }

                    // C. RESPUESTAS
                    var datosPlanos = new List<dynamic>();
                    string sqlD = @"
                SELECT h.""Id_Respuesta"", h.""Fecha"", h.""Id_Usuario"",
                       d.""Id_Pregunta"", d.""Valor_Respuesta"",
                       CASE WHEN h.""Id_Usuario"" > 0 THEN u.""NombreCompleto"" 
                            ELSE COALESCE(h.""Nombre_Externo"", 'Anónimo') 
                       END as ""Nombre_Final"",
                       CASE WHEN h.""Id_Usuario"" > 0 THEN u.""Email"" 
                            ELSE COALESCE(h.""Email_Externo"", '') 
                       END as ""Email_Final"",
                       CASE WHEN h.""Id_Usuario"" > 0 THEN 'Sí' ELSE 'No' END as ""Es_Logueado"",
                       ig.nombre as ""Nombre_Iglesia"", m.nombre as ""Municipio"", ig.localidad as ""Localidad""
                FROM ""Encuestas_Respuestas_Header"" h
                LEFT JOIN ""Encuestas_Respuestas_Detalle"" d ON h.""Id_Respuesta"" = d.""Id_Respuesta""
                LEFT JOIN ""Sist_Usuarios"" u ON h.""Id_Usuario"" = u.""Id_Usuario"" 
                LEFT JOIN iciar_iglesias ig ON h.""Id_Iglesia_Seleccionada"" = ig.id
                LEFT JOIN iciar_municipios m ON ig.municipio_id = m.id
                WHERE h.""Id_Encuesta"" = @id
                ORDER BY h.""Fecha"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlD, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                string nIglesia = r["Nombre_Iglesia"] != DBNull.Value ? r["Nombre_Iglesia"].ToString() : "";
                                string nMunicipio = r["Municipio"] != DBNull.Value ? r["Municipio"].ToString() : "";
                                string nLocalidad = r["Localidad"] != DBNull.Value ? r["Localidad"].ToString() : "";

                                string txtLoc = !string.IsNullOrEmpty(nLocalidad) ? $", {nLocalidad}" : "";
                                string iglesiaAsignada = !string.IsNullOrEmpty(nIglesia) ? $"{nIglesia} - {nMunicipio}{txtLoc}" : "No especificada";

                                datosPlanos.Add(new
                                {
                                    IdRespuesta = (int)r["Id_Respuesta"],
                                    Fecha = (DateTime)r["Fecha"],
                                    Nombre = esAnonima ? $"Anónimo #{r["Id_Respuesta"]}" : r["Nombre_Final"].ToString(),
                                    Email = esAnonima ? "Oculto" : r["Email_Final"].ToString(),
                                    EsLogueado = r["Es_Logueado"].ToString(),
                                    Iglesia = iglesiaAsignada,
                                    IdPregunta = r["Id_Pregunta"] == DBNull.Value ? (int?)null : (int)r["Id_Pregunta"],
                                    Valor = r["Valor_Respuesta"] == DBNull.Value ? "" : r["Valor_Respuesta"].ToString()
                                });
                            }
                        }
                    }

                    // D. Generar Excel
                    using (var workbook = new XLWorkbook())
                    {
                        var ws = workbook.Worksheets.Add("Resultados");
                        int col = 1;
                        ws.Cell(1, col++).Value = "ID";
                        ws.Cell(1, col++).Value = "Fecha";
                        ws.Cell(1, col++).Value = "Usuario Logueado";
                        ws.Cell(1, col++).Value = "Participante";
                        ws.Cell(1, col++).Value = "Email";
                        ws.Cell(1, col++).Value = "Iglesia";

                        var mapColumna = new Dictionary<int, int>();
                        foreach (var p in columnasPreguntas)
                        {
                            ws.Cell(1, col).Value = p.Texto;
                            mapColumna[p.Id] = col++;
                        }

                        var rangoHeader = ws.Range(1, 1, 1, col - 1);
                        rangoHeader.Style.Font.Bold = true;
                        rangoHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#E0F7FA");

                        var grupos = datosPlanos.GroupBy(x => (int)x.IdRespuesta);
                        int row = 2;

                        foreach (var g in grupos)
                        {
                            var header = g.First();
                            ws.Cell(row, 1).Value = (int)header.IdRespuesta;
                            ws.Cell(row, 2).Value = (DateTime)header.Fecha;
                            ws.Cell(row, 3).Value = (string)header.EsLogueado;
                            ws.Cell(row, 4).Value = (string)header.Nombre;
                            ws.Cell(row, 5).Value = (string)header.Email;
                            ws.Cell(row, 6).Value = (string)header.Iglesia;

                            foreach (var detalle in g)
                            {
                                int? idPreg = (int?)detalle.IdPregunta;
                                if (idPreg.HasValue && mapColumna.ContainsKey(idPreg.Value))
                                {
                                    string val = (string)detalle.Valor;
                                    if (!string.IsNullOrEmpty(val)) val = val.Replace("|", ", ");

                                    if (double.TryParse(val, out double numero))
                                        ws.Cell(row, mapColumna[idPreg.Value]).Value = numero;
                                    else
                                        ws.Cell(row, mapColumna[idPreg.Value]).Value = val;
                                }
                            }
                            row++;
                        }

                        ws.Columns().AdjustToContents();

                        using (var stream = new MemoryStream())
                        {
                            workbook.SaveAs(stream);
                            string nombreLimpio = string.Join("_", tituloEncuesta.Split(Path.GetInvalidFileNameChars()));
                            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Resultados_{nombreLimpio}_{DateTime.Now:yyyyMMdd}.xlsx");
                        }
                    }
                }
            }
            catch (Exception ex) { return Content($"Error: {ex.Message}"); }
        }

        private async Task<string> SubirArchivoCloudinary(IFormFile archivo, string folderPath)
        {
            var ext = Path.GetExtension(archivo.FileName).ToLower();
            bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp";

            using var memoryStream = new MemoryStream();

            if (isImage)
            {
                // Si es imagen, la comprimimos con ImageSharp
                using (var image = await SixLabors.ImageSharp.Image.LoadAsync(archivo.OpenReadStream()))
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
                    File = new FileDescription(archivo.FileName, memoryStream),
                    Folder = folderPath,
                    UseFilename = false,
                    UniqueFilename = true
                };
                var result = await _cloudinary.UploadAsync(uploadParams);
                return result.SecureUrl.ToString();
            }
            else
            {
                // Si es documento (PDF, DOCX), se sube intacto como RAW
                await archivo.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var uploadParams = new RawUploadParams()
                {
                    File = new FileDescription(archivo.FileName, memoryStream),
                    Folder = folderPath,
                    UseFilename = false,
                    UniqueFilename = true
                };
                var result = await _cloudinary.UploadAsync(uploadParams);
                return result.SecureUrl.ToString();
            }
        }

        /// <summary>
        /// DTO interno para mapear las preguntas de la encuesta
        /// </summary>
        private class PreguntaMapDTO
        {
            public int Id { get; set; }
            public string Texto { get; set; }
            public string Tipo { get; set; }
            public int Orden { get; set; }
        }

        /// <summary>
        /// DTO interno para mapear respuestas planas
        /// </summary>
        private class RespuestaPlanaDTO
        {
            public int IdPregunta { get; set; }
            public string Valor { get; set; }
            public int IdRespuesta { get; set; }
        }
    }
}