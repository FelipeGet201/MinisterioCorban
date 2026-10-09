using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using RedAJP.Globales;
using BCrypt.Net;

namespace RedAJP.Controllers
{
    public class LoginController : GlobalController
    {
        private readonly IConfiguration _configuration;

        public LoginController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Muestra la vista de login
        /// </summary>
        /// <returns>Retorna la vista de login</returns>
        public IActionResult Index(string returnUrl = null)
        {
            if (User.Identity!.IsAuthenticated)
            {
                // Si ya está logueado y hay una URL de retorno válida, mándalo ahí
                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return LocalRedirect(returnUrl);

                return RedirectToAction("Index", "Eventos");
            }

            // Pasamos la URL a la vista para esconderla en el formulario
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        /// <summary>
        /// Procesa el login
        /// </summary>
        /// <param name="user">Es el nombre de usuario o email</param>
        /// <param name="password">Es la contraseña</param>
        /// <returns>Retorna la vista de login o redirige a la tienda</returns>
        [HttpPost]
        public async Task<IActionResult> Index(string user, string password, string returnUrl = null)
        {
            // 1. VALIDACIÓN
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
            {
                MostrarMensaje("¡Atención!", "Por favor llena todos los campos.", TipoMensaje.Alerta);
                ViewBag.ReturnUrl = returnUrl;
                ViewBag.UsuarioIntentado = user;
                return View();
            }

            string connectionString = _configuration.GetConnectionString("MiConexion");

            try
            {
                using (var conexion = new NpgsqlConnection(connectionString))
                {
                    await conexion.OpenAsync();

                    // 2. BUSCAR USUARIO (Modificado para seguridad híbrida)
                    // traemos el password para verificar en C#
                    string sqlUsuario = @"
                SELECT u.""Email_Verificado"", u.""Id_Usuario"", u.""NombreCompleto"", u.""Email"", 
                       u.""Id_Rol"", u.""Sello_Seguridad"", u.""PasswordHash"",
                       r.""Nombre"" as ""Nombre_Rol""
                FROM ""Sist_Usuarios"" u
                INNER JOIN ""Sist_Roles"" r ON u.""Id_Rol"" = r.""Id_Rol""
                WHERE (LOWER(u.""Nombre_Usuario"") = LOWER(@usuario) OR LOWER(u.""Email"") = LOWER(@usuario)) 
                  AND u.""Activo"" = true";

                    string idUser = "", nombre = "", email = "", idRol = "", rol = "", sello = "";
                    bool usuarioEncontrado = false;
                    bool necesitaMigracion = false; // Bandera para detectar usuarios con clave vieja
                    // idIglesiaAsignada no aplica en este proyecto

                    using (var cmd = new NpgsqlCommand(sqlUsuario, conexion))
                    {
                        cmd.Parameters.AddWithValue("@usuario", user);

                        using (var lector = await cmd.ExecuteReaderAsync())
                        {
                            if (lector.Read()) // USUARIO ENCONTRADO
                            {
                                // --- LÓGICA DE VERIFICACIÓN ---
                                string hashBD = lector["PasswordHash"].ToString();
                                bool passValido = false;

                                // A. ¿Es un Hash seguro? (BCrypt)
                                if (!string.IsNullOrEmpty(hashBD) && hashBD.StartsWith("$2"))
                                {
                                    passValido = BCrypt.Net.BCrypt.Verify(password, hashBD);
                                }
                                // B. Es Texto Plano (Usuario Antiguo - Legacy)
                                else
                                {
                                    // Comparamos texto plano Se comentó pues ya no se usa pero podria mas adelante..
                                    if (hashBD == password)
                                    {
                                        passValido = true;
                                        necesitaMigracion = true; // Marcar para encriptar
                                    }
                                }

                                if (passValido)
                                {
                                    usuarioEncontrado = true;

                                    bool emailVerificado = (bool)lector["Email_Verificado"];
                                    // Importante: No retornamos inmediatamente si no está verificado para no filtrar información
                                    // Pero por lógica de negocio, aquí mostramos el mensaje.
                                    if (emailVerificado == false)
                                    {
                                        MostrarMensaje("Verificación Pendiente", "Tu cuenta no está verificada.", TipoMensaje.Alerta);
                                        ViewBag.UsuarioIntentado = user;
                                        return View();
                                    }

                                    sello = lector["Sello_Seguridad"].ToString();
                                    idUser = lector["Id_Usuario"].ToString();
                                    nombre = lector["NombreCompleto"].ToString();
                                    email = lector["Email"].ToString();
                                    idRol = lector["Id_Rol"].ToString();
                                    rol = lector["Nombre_Rol"].ToString();
                                    // lector Id_Iglesia_Asignada removido
                                }
                            }
                            else
                            {
                                // --- CORRECCIÓN: PROTECCIÓN CONTRA TIMING ATTACKS ---
                                // Si el usuario NO existe, ejecutamos un hash falso.
                                // Esto consume el mismo tiempo de CPU que si existiera, confundiendo al atacante.

                                // Hash dummy (costo 11, similar al real)
                                string dummyHash = "$2a$11$Z5GkhNMj8t.hu5bXjFz7.u7.u7.u7.u7.u7.u7.u7.u7.u7.u7.";
                                BCrypt.Net.BCrypt.Verify("dummy_password", dummyHash);

                                usuarioEncontrado = false;
                            }
                        }
                    }

                    if (usuarioEncontrado)
                    {


                        // 3. AUTO-REPARACIÓN (Migración Silenciosa)
                        // Si el usuario tenía clave de texto plano, la encriptamos ahora mismo
                        if (necesitaMigracion)
                        {
                            string nuevoHash = BCrypt.Net.BCrypt.HashPassword(password);
                            string sqlFix = "UPDATE \"Sist_Usuarios\" SET \"PasswordHash\" = @nh WHERE \"Id_Usuario\" = @id";
                            using (var cmdFix = new NpgsqlCommand(sqlFix, conexion))
                            {
                                cmdFix.Parameters.AddWithValue("@nh", nuevoHash);
                                cmdFix.Parameters.AddWithValue("@id", int.Parse(idUser));
                                await cmdFix.ExecuteNonQueryAsync();
                            }
                        }


                        // 4. CARGAR PERMISOS
                        var claims = new List<Claim>
                        {
                            new Claim("SelloSeguridad", sello),
                            new Claim(ClaimTypes.Name, nombre),
                            new Claim(ClaimTypes.Email, email),
                            new Claim(ClaimTypes.NameIdentifier, user),
                            new Claim("IdUsuario", idUser),
                            new Claim(ClaimTypes.Role, rol)
                        };

                        string sqlPermisos = @"
                    SELECT m.""Nombre_Clave"", p.""P_Leer"", p.""P_Crear"", p.""P_Editar"", p.""P_Borrar"", p.""P_Admin""
                    FROM ""Sist_Permisos"" p
                    INNER JOIN ""Sist_Modulos"" m ON p.""Id_Modulo"" = m.""Id_Modulo""
                    WHERE p.""Id_Rol"" = @idRol";

                        using (var cmdPerm = new NpgsqlCommand(sqlPermisos, conexion))
                        {
                            cmdPerm.Parameters.AddWithValue("@idRol", int.Parse(idRol));

                            using (var lectorPerm = await cmdPerm.ExecuteReaderAsync())
                            {
                                while (lectorPerm.Read())
                                {
                                    string modulo = lectorPerm["Nombre_Clave"].ToString();
                                    bool p_Leer = (bool)lectorPerm["P_Leer"];

                                    if (p_Leer) claims.Add(new Claim("Permiso", modulo));

                                    // Lógica de banderas detalladas...
                                    List<string> banderas = new List<string>();
                                    if ((bool)lectorPerm["P_Leer"]) banderas.Add(Parametros.Permisos.Leer.Valor);
                                    if ((bool)lectorPerm["P_Crear"]) banderas.Add(Parametros.Permisos.Crear.Valor);
                                    if ((bool)lectorPerm["P_Editar"]) banderas.Add(Parametros.Permisos.Editar.Valor);
                                    if ((bool)lectorPerm["P_Borrar"]) banderas.Add(Parametros.Permisos.Borrar.Valor);
                                    if ((bool)lectorPerm["P_Admin"]) banderas.Add(Parametros.Permisos.Admin.Valor);

                                    string valorDetallado = $"{modulo}|{string.Join(",", banderas)}";
                                    claims.Add(new Claim("PermisoDetalle", valorDetallado));
                                }
                            }
                        }



                        // 5. LOGIN EXITOSO
                        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));

                        MostrarMensaje("¡HOLA!", $"Bienvenido, {nombre}", TipoMensaje.Exito);

                        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                        {
                            return LocalRedirect(returnUrl);
                        }

                        TempData["ResetMascota"] = "true";
                        return RedirectToAction("Index", "Eventos");
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "Ocurrió un error: " + ex.Message, TipoMensaje.Error);
                ViewBag.ReturnUrl = returnUrl;
                ViewBag.UsuarioIntentado = user;
                return View();
            }

            // 6. FALLO DE LOGIN
            MostrarMensaje("¡Ups!", "Usuario o contraseña incorrectos.", TipoMensaje.Error);
            ViewBag.UsuarioIntentado = user;
            return View();
        }

        /// <summary>
        /// Cierra la sesión del usuario
        /// </summary>
        /// <returns>Redirige a la vista de login</returns>
        public async Task<IActionResult> Salir()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            TempData.Clear();
            //MostrarMensaje("Hasta pronto", "Has cerrado sesión correctamente.", TipoMensaje.Exito);
            return RedirectToAction("Index", "Login");
        }

        /// <summary>
        /// Muestra la vista para recuperar la contraseña
        /// </summary>
        /// <returns>Retorna la vista de recuperación</returns>
        public IActionResult Recuperar()
        {
            if (User.Identity!.IsAuthenticated) return RedirectToAction("Index", "Eventos");
            return View();
        }

        /// <summary>
        /// Procesa la solicitud de recuperación de contraseña
        /// </summary>
        /// <param name="email">Es el correo electrónico del usuario</param>
        /// <returns>Retorna la vista de recuperación o redirige al login</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Recuperar(string email)
        {
            if (string.IsNullOrEmpty(email))
            {
                MostrarMensaje("Atención", "Por favor ingresa tu correo electrónico.", TipoMensaje.Alerta);
                return View();
            }

            string connectionString = _configuration.GetConnectionString("MiConexion");

            try
            {
                using (var conexion = new NpgsqlConnection(connectionString))
                {
                    await conexion.OpenAsync();

                    // CORRECCIÓN AQUÍ:
                    // 1. Usamos LOWER() para ignorar mayúsculas.
                    // 2. Solo buscamos por la columna "Email", ignorando "Nombre_Usuario".
                    string sqlBuscar = @"SELECT ""Id_Usuario"", ""NombreCompleto"" 
                                 FROM ""Sist_Usuarios"" 
                                 WHERE LOWER(""Email"") = LOWER(@em) AND ""Activo"" = true";

                    int idUsuario = 0;
                    string nombre = "";

                    using (var cmd = new NpgsqlCommand(sqlBuscar, conexion))
                    {
                        cmd.Parameters.AddWithValue("@em", email.Trim()); // Trim() quita espacios en blanco accidentales al inicio/final
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (reader.Read())
                            {
                                idUsuario = (int)reader["Id_Usuario"];
                                nombre = reader["NombreCompleto"].ToString();
                            }
                        }
                    }

                    // Si el usuario existe, enviamos el correo
                    if (idUsuario > 0)
                    {
                        using (var transaccion = await conexion.BeginTransactionAsync())
                        {
                            try
                            {
                                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                                // Llamamos a la función de envío (la misma que te pasé en la respuesta anterior)
                                bool enviado = await EnviarCorreoRecuperacion(_configuration, conexion, transaccion, idUsuario, nombre, email, ip);

                                if (enviado) await transaccion.CommitAsync();
                                else { await transaccion.RollbackAsync(); return View(); }
                            }
                            catch { await transaccion.RollbackAsync(); throw; }
                        }
                    }

                    // Mensaje genérico por seguridad (para no revelar qué correos existen)
                    MostrarMensaje("Solicitud Recibida", "Si el correo es correcto, recibirás un enlace para recuperar tu contraseña.", TipoMensaje.Exito);
                    return RedirectToAction("Index");
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error: " + ex.Message, TipoMensaje.Error);
                return View();
            }
        }

        /// <summary>
        /// Envía el correo de recuperación de contraseña
        /// </summary>
        /// <param name="config">Es la configuración de la aplicación, se solicita para el envío de correos</param>
        /// <param name="conexion">Es la conexión abierta a la base de datos</param>
        /// <param name="transaccion">Es la transacción activa</param>
        /// <param name="idUsuario">Es el Id del usuario que solicita la recuperación</param>
        /// <param name="nombre">Es el nombre del usuario</param>
        /// <param name="email">Es el correo electrónico del usuario</param>
        /// <param name="ipUsuario">Es la IP desde donde se solicita la recuperación</param>
        /// <returns>Retorna true si el correo se envió correctamente, false en caso contrario</returns>
        private async Task<bool> EnviarCorreoRecuperacion(IConfiguration config, NpgsqlConnection conexion, NpgsqlTransaction transaccion, int idUsuario, string nombre, string email, string ipUsuario)
        {
            try
            {
                // 1. Limpiar tokens viejos
                string sqlDel = "DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\" = @id";
                using (var cmd = new NpgsqlCommand(sqlDel, conexion, transaccion))
                {
                    cmd.Parameters.AddWithValue("@id", idUsuario);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 2. Crear nuevo token
                string token = Guid.NewGuid().ToString();
                string sqlTok = @"INSERT INTO ""Sist_Verificaciones"" (""Id_Usuario"", ""Token"", ""Fecha_Expiracion"") 
                          VALUES (@id, @tok, NOW() + INTERVAL '2 hours')"; // 2 horas de validez para password

                using (var cmd = new NpgsqlCommand(sqlTok, conexion, transaccion))
                {
                    cmd.Parameters.AddWithValue("@id", idUsuario);
                    cmd.Parameters.AddWithValue("@tok", token);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 3. Construir Correo
                // Nota: Asegúrate de tener una Acción 'Restablecer' que reciba el token
                string link = Url.Action("Restablecer", "Login", new { token = token }, Request.Scheme);

                string cuerpoHTML = $@"
        <div style='font-family: Arial, sans-serif; padding: 20px; background-color: #f4f4f4;'>
            <div style='max-width: 500px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; border: 1px solid #ddd; border-top: 4px solid #d4af37;'>
                <h2 style='margin-top: 0; color: #00334e;'>Recuperar Contraseña</h2>
                <p>Hola <strong>{nombre}</strong>,</p>
                <p>Hemos recibido una solicitud para restablecer tu contraseña. Haz clic en el siguiente botón para crear una nueva:</p>
                <br>
                <div style='text-align: center;'>
                    <a href='{link}' style='background-color: #00334e; color: #d4af37; padding: 12px 24px; text-decoration: none; border-radius: 50px; font-weight: bold;'>RESTABLECER CONTRASEÑA</a>
                </div>
                <br>
                <p style='font-size: 13px; color: #666;'>
                    Si no solicitaste este cambio, puedes ignorar este correo. El enlace expirará en 2 horas.
                </p>
            </div>
        </div>";

                // 4. Envío usando tu clase Global Funciones
                var (enviado, errorDetalle) = await Funciones.EnviarCorreo(config, email, "Restablecer Contraseña", cuerpoHTML);

                if (!enviado)
                {
                    // Opcional: Loguear error como en RegistroController
                    Console.WriteLine($"Error enviando correo recuperación: {errorDetalle}");
                }

                return enviado;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error Token Recuperación: " + ex.Message);
                return false;
            }
        }
        /// <summary>
        /// Muestra la vista para restablecer la contraseña
        /// </summary>
        /// <param name="token">Es el token de recuperación enviado por correo</param>
        /// <returns>Retorna la vista de restablecimiento o redirige a recuperación</returns>
        [HttpGet]
        public async Task<IActionResult> Restablecer(string token)
        {
            // Si no hay token, lo mandamos al login
            if (string.IsNullOrEmpty(token)) return RedirectToAction("Index");

            try
            {
                using (var conexion = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await conexion.OpenAsync();

                    // Buscamos si el token existe y NO ha expirado
                    string sqlValidar = @"SELECT ""Id_Usuario"" FROM ""Sist_Verificaciones"" 
                                  WHERE ""Token"" = @t AND ""Fecha_Expiracion"" > NOW()";

                    using (var cmd = new NpgsqlCommand(sqlValidar, conexion))
                    {
                        cmd.Parameters.AddWithValue("@t", token);
                        var resultado = await cmd.ExecuteScalarAsync();

                        if (resultado != null)
                        {
                            // ¡Token válido! Guardamos el token para enviarlo en el POST
                            ViewBag.Token = token;
                            return View();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Loguear error internamente si es necesario
                Console.WriteLine(ex.Message);
            }

            // Si llegamos aquí, el token es inválido o expiró
            MostrarMensaje("Enlace Caducado", "El enlace de recuperación es inválido o ha expirado. Por favor solicita uno nuevo.", TipoMensaje.Error);
            return RedirectToAction("Recuperar"); // Lo mandamos a pedir el correo de nuevo
        }

        /// <summary>
        /// Procesa el restablecimiento de la contraseña
        /// </summary>
        /// <param name="token">Es el token de recuperación</param>
        /// <param name="password">Es la nueva contraseña</param>
        /// <param name="confirmPassword">Es la confirmación de la nueva contraseña</param>
        /// <returns>Retorna la vista de restablecimiento o redirige al login</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restablecer(string token, string password, string confirmPassword)
        {
            // A. Validaciones básicas
            if (string.IsNullOrEmpty(token)) return RedirectToAction("Index");

            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(confirmPassword))
            {
                MostrarMensaje("Atención", "Todos los campos son obligatorios.", TipoMensaje.Alerta);
                ViewBag.Token = token;
                return View();
            }

            if (password != confirmPassword)
            {
                MostrarMensaje("Error", "Las contraseñas no coinciden.", TipoMensaje.Error);
                ViewBag.Token = token;
                return View();
            }

            // B. Proceso de actualización
            try
            {
                using (var conexion = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await conexion.OpenAsync();

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Validar token y obtener Id_Usuario
                            string sqlCheck = @"SELECT ""Id_Usuario"" FROM ""Sist_Verificaciones"" 
                                WHERE ""Token"" = @t AND ""Fecha_Expiracion"" > NOW()";

                            int idUsuario = 0;
                            using (var cmd = new NpgsqlCommand(sqlCheck, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@t", token);
                                var res = await cmd.ExecuteScalarAsync();
                                if (res != null) idUsuario = (int)res;
                            }

                            if (idUsuario > 0)
                            {
                                // 2. Actualizar la contraseña (ENCRIPTADA)
                                string sqlUpdate = @"UPDATE ""Sist_Usuarios"" 
                                     SET ""PasswordHash"" = @p, ""Fecha_Modificacion"" = NOW(), ""Sello_Seguridad"" = gen_random_uuid()
                                     WHERE ""Id_Usuario"" = @id";

                                // NOTA: Agregué actualización del Sello_Seguridad para cerrar sesiones abiertas en otros dispositivos

                                using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion, transaccion))
                                {
                                    string passwordHash = BCrypt.Net.BCrypt.HashPassword(password);
                                    cmdUpd.Parameters.AddWithValue("@p", passwordHash);
                                    cmdUpd.Parameters.AddWithValue("@id", idUsuario);
                                    await cmdUpd.ExecuteNonQueryAsync();
                                }

                                // 3. Quema el token
                                string sqlBurn = @"DELETE FROM ""Sist_Verificaciones"" WHERE ""Id_Usuario"" = @id";
                                using (var cmdDel = new NpgsqlCommand(sqlBurn, conexion, transaccion))
                                {
                                    cmdDel.Parameters.AddWithValue("@id", idUsuario);
                                    await cmdDel.ExecuteNonQueryAsync();
                                }

                                // --- 4. REGISTRAR EN BITÁCORA ---
                                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                                // Usamos la acción 'Editar' o 'RestablecerPassword' si la tienes definida en tus Parámetros
                                await Funciones.RegistrarBitacora(conexion, idUsuario,
                                    Parametros.Modulos.Usuarios,
                                    Parametros.AccionesBitacora.Editar, // O usa una acción específica si existe
                                    "Restableció su contraseña mediante recuperación por correo",
                                    ip, transaccion);
                                // ----------------------------------------

                                await transaccion.CommitAsync();

                                MostrarMensaje("¡Contraseña Restablecida!", "Tu contraseña ha sido actualizada correctamente. Inicia sesión.", TipoMensaje.Exito);
                                return RedirectToAction("Index");
                            }
                            else
                            {
                                await transaccion.RollbackAsync();
                                MostrarMensaje("Error", "El tiempo de espera ha expirado. Intenta de nuevo.", TipoMensaje.Error);
                                return RedirectToAction("Recuperar");
                            }
                        }
                        catch (Exception ex)
                        {
                            await transaccion.RollbackAsync();
                            throw ex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "No pudimos actualizar tu contraseña: " + ex.Message, TipoMensaje.Error);
                ViewBag.Token = token;
                return View();
            }
        }
    }
}