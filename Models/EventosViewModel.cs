using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace RedAJP.Models
{
    // =========================================================
    // ENUMS Y AUXILIARES
    // =========================================================
    public enum TipoPregunta { Texto = 1, Booleano = 2, OpcionMultiple = 3 }

    // =========================================================
    // 1. VISTA GENERAL (INDEX / CALENDARIO)
    // =========================================================
    public class CalendarioViewModel
    {
        public EventoItemViewModel EventoDestacado { get; set; }
        public List<EventoItemViewModel> ListaEventos { get; set; } = new List<EventoItemViewModel>();
    }
    public class ProductoExtraItem
    {
        public int Id_ProductoExtra { get; set; }
        public int? Id_Subtipo { get; set; }
        public string Nombre_Producto { get; set; }
        public string Descripcion { get; set; }
        public decimal Precio { get; set; }
        public int Cantidad_Minima { get; set; }
        public int Cantidad_Maxima { get; set; }
        public bool Activo { get; set; }
    }

    public class SubtipoEventoItem
    {
        public int Id_Subtipo { get; set; }
        public string Nombre { get; set; }
        public decimal Costo { get; set; }
        public int Cupo { get; set; }
        public bool Activo { get; set; }

        // Helper para mostrar cupo restante en vistas
        public int Ocupados { get; set; }
        public int Disponibles => Math.Max(0, Cupo - Ocupados);
    }

    public class FotoGaleriaItem
    {
        public int Id_Foto { get; set; }
        public string Url_Foto { get; set; } // Se usará para el <IMG> (Thumbnail)
        public string Descripcion { get; set; }
        public int Orden { get; set; }

        // NUEVO: Para el enlace <A HREF> (Preview)
        public string Url_Link_Preview { get; set; }
    }

    public class InscriptorItem
    {
        public int Id_Usuario { get; set; }
        public string NombreUsuario { get; set; } // Para mostrar en el Editor
    }

    public class EventoItemViewModel
    {
        public int Id_Evento { get; set; }
        public string Id_Encriptado_Evento { get; set; }
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public DateTime Fecha_Inicio { get; set; }
        public DateTime Fecha_Fin { get; set; }
        public decimal Costo { get; set; } // Costo "Desde" si hay subtipos

        public bool Requiere_Registro { get; set; }
        public int Tipo_Registro { get; set; } // 1=Individual, 2=Grupal

        public bool Es_Destacado { get; set; }

        public string Imagen_Url { get; set; } // Se usará para el <IMG> (Thumbnail)

        // NUEVO: Para el enlace <A HREF> (Preview)
        public string Imagen_Link_Preview { get; set; }

        public int MiCantidadConfirmada { get; set; }
        public int MiCantidadPendiente { get; set; }
        public decimal MiMontoDeuda { get; set; }
        public decimal MiMontoFuturo { get; set; }
        public int IdSolicitudPendiente { get; set; }

        public int Cupo_Maximo { get; set; }
        public int TotalOcupados { get; set; }
        public int TotalAsistentesGlobal { get; set; }

        public bool HayCupo => Cupo_Maximo == 0 || TotalOcupados < Cupo_Maximo;
        public bool Permitir_Pago { get; set; }

        public string MesAbreviado => Fecha_Inicio.ToString("MMM", new CultureInfo("es-ES")).ToUpper();
        public string DiaNumero => Fecha_Inicio.Day.ToString("00");
        public string DiaSemHoraFormato => Fecha_Inicio.ToString("ddd h:mm tt", new CultureInfo("es-ES"));
        public string SoloHoraFormato => Fecha_Inicio.ToString("h:mm tt", new CultureInfo("es-ES"));

        public bool Activo { get; set; }

        public bool EsPrivado { get; set; }
        public bool TengoPermisoInscribir { get; set; }
        public bool Bloqueado { get; set; }

    }

    // =========================================================
    // 2. EDITOR DE EVENTOS (ADMIN)
    // =========================================================
    public class EditorEventoViewModel
    {
        public int Id_Evento { get; set; }

        [Required(ErrorMessage = "El título es obligatorio")]
        public string Titulo { get; set; }

        public string Descripcion { get; set; }

        [Required]
        public DateTime Fecha_Inicio { get; set; } = DateTime.Now.Date.AddDays(1).AddHours(9);
        public DateTime Fecha_Fin { get; set; } = DateTime.Now.Date.AddDays(1).AddHours(13);

        public decimal Costo_Entrada { get; set; }
        public bool Requiere_Registro { get; set; }
        public int Tipo_Registro { get; set; } = 1;
        public int Cupo_Maximo { get; set; }

        [Url(ErrorMessage = "Debe ser una URL válida")]
        public string Imagen_Url { get; set; }

        public bool Es_Destacado { get; set; }
        public bool Activo { get; set; } = true;
        public bool Permitir_Pago { get; set; }

        public List<PreguntaConfigitem> Preguntas { get; set; } = new List<PreguntaConfigitem>();

        public bool Permitir_Pagos_Parciales { get; set; }
        public List<CalendarioPagoItem> CalendarioPagos { get; set; } = new List<CalendarioPagoItem>();

        // --- NUEVOS CAMPOS ---
        public string Clave_Cuenta_Bancaria { get; set; } = "DEFAULT";
        public List<SubtipoEventoItem> Subtipos { get; set; } = new List<SubtipoEventoItem>();
        public List<FotoGaleriaItem> Galeria { get; set; } = new List<FotoGaleriaItem>();
        public List<ProductoExtraItem> ProductosExtra { get; set; } = new List<ProductoExtraItem>();

        // IDs de usuarios que son inscriptores (se llenará con un input separado por comas o modal de búsqueda)
        // Para simplificar el binding, usaremos una lista de enteros o un string parseable
        public string IdsInscriptores { get; set; }
        // Para mostrar los nombres actuales en la vista
        public List<InscriptorItem> InscriptoresActuales { get; set; } = new List<InscriptorItem>();
        public bool Es_Privado { get; set; }
        public bool Permitir_Transferencia { get; set; }
        public bool Permitir_Pago_Tarjeta { get; set; }
        public bool Cobrar_Comision_Extra { get; set; } = true;
        public int Id_Grupo_Usuarios { get; set; }
        public bool Bloqueado { get; set; }
        public int? Id_Encuesta_Requisito { get; set; }
    }

    public class CalendarioPagoItem
    {
        public int Id_Calendario_Pago { get; set; }
        public int Id_Subtipo { get; set; }
        public string Nombre_Concepto { get; set; }
        public int Numero_Pago { get; set; }
        public DateTime Fecha_Limite { get; set; }
        public decimal Monto { get; set; }
        public bool Eliminado { get; set; }

    }

    public class PreguntaConfigitem
    {
        public int Id_Pregunta { get; set; }
        public string Texto { get; set; }
        public TipoPregunta Tipo { get; set; }
        public bool Eliminada { get; set; }

        // Nuevo: Opciones separadas por '|'
        public string Opciones { get; set; }
    }

    // =========================================================
    // 3. REGISTRO (VISTA FORMULARIO USUARIO)
    // =========================================================
    public class RegistroEventoViewModel
    {
        public int Id_Evento { get; set; }
        public string TituloEvento { get; set; }
        public decimal CostoUnitario { get; set; } // Costo base o general
        public int TipoRegistro { get; set; }

        public List<PreguntaDefinicion> PreguntasDefinidas { get; set; } = new List<PreguntaDefinicion>();
        public List<AsistenteItem> Asistentes { get; set; } = new List<AsistenteItem>();

        public List<SubtipoEventoItem> SubtiposDisponibles { get; set; } = new List<SubtipoEventoItem>();
    }

    public class PreguntaDefinicion
    {
        public int Id_Pregunta { get; set; }
        public string Texto { get; set; }
        public TipoPregunta Tipo { get; set; }
        public List<string> Opciones { get; set; } = new List<string>(); // Parseado de la BD
    }

    public class AsistenteItem
    {
        public int Id_Asistente { get; set; }
        public string NombreCompleto { get; set; }
        public string Genero { get; set; } = "H";
        public string EtiquetaGrupo { get; set; } = "";
        public int Edad { get; set; } = 18;
        public List<RespuestaItem> Respuestas { get; set; } = new List<RespuestaItem>();

        // Nuevo: Selección individual de subtipo (ej. uno va a cabaña, otro a camping)
        public int? Id_Subtipo_Seleccionado { get; set; }
        public List<AsistenteProductoExtraItem> ProductosExtra { get; set; } = new List<AsistenteProductoExtraItem>();

        public string TokenExterno { get; set; }
    }

    public class RespuestaItem
    {
        public int Id_Pregunta { get; set; }
        public string Valor { get; set; }
    }

    public class AsistenteProductoExtraItem
    {
        public int Id_ProductoExtra { get; set; }
        public int Cantidad { get; set; }
    }

    // =========================================================
    // 4. VISTA DE DETALLES (INFO)
    // =========================================================
    public class DetalleEventoViewModel
    {
        public EventoItemViewModel Evento { get; set; }
        public int TotalAsistentesGlobal { get; set; }

        public List<FotoGaleriaItem> Galeria { get; set; } = new List<FotoGaleriaItem>();
        public List<SubtipoEventoItem> Subtipos { get; set; } = new List<SubtipoEventoItem>();
        public bool MostrarBotonAlojamiento { get; set; }
        
        // Propiedades de la encuesta requisito
        public bool TieneEncuestaRequisito { get; set; }
        public int IdEncuestaRequisito { get; set; }
        public string UrlEncuestaRequisito { get; set; }
        public bool EncuestaYaRespondida { get; set; }
    }

    // =========================================================
    // 5. CONFIRMAR PAGO / PAGO EXTERNO (VIEWMODEL UNIFICADO)
    // =========================================================
    public class ConfirmarPagoViewModel
    {
        public int IdEvento { get; set; }
        public string TituloEvento { get; set; }
        public DateTime FechaEvento { get; set; }
        public decimal SubTotal { get; set; }
        public decimal ComisionPlataforma { get; set; }
        public bool CobrarComisionExtra { get; set; } = true;
        public decimal TotalPagar { get; set; }
        public int CantidadPersonas { get; set; }
        public string PublicKeyMP { get; set; }
        public string PreferenceId { get; set; }
        public List<AgrupacionDeudaUsuario> ListadoPorUsuario { get; set; } = new List<AgrupacionDeudaUsuario>();
        public List<string> IdsPagosSeleccionados { get; set; } = new List<string>();
        public bool MostrarPagosFuturos { get; set; }
        public List<string> NombresAsistentes { get; set; } = new List<string>();
        public List<DetalleDeudaItem> DesgloseDeudas { get; set; } = new List<DetalleDeudaItem>();
        public string UrlPasarelaPago { get; set; }

        // --- CAMPOS NUEVOS PARA CUPONES ---
        public decimal DescuentoAplicado { get; set; } // El monto descontado
        public string CodigoCuponAplicado { get; set; } // El código usado (ej: VERANO2026)
        public string MensajeCupon { get; set; } // Mensaje de éxito o error
    }


    public class AgrupacionDeudaUsuario
    {
        public int IdRegistro { get; set; }
        public int IdAsistente { get; set; }
        public string NombreAsistente { get; set; }
        public List<ItemDeudaPago> Pagos { get; set; } = new List<ItemDeudaPago>();
        public bool EstaTotalmentePagado { get; set; }
        public string TokenExterno { get; set; }

        // Nuevo: Para mostrar en qué modalidad está inscrito en el resumen de deuda
        public string NombreSubtipo { get; set; }
        public bool EncuestaRespondida { get; set; } = true;
    }

    public class ItemDeudaPago
    {
        public int IdCalendario { get; set; }
        public int NumeroPago { get; set; }
        public string Concepto { get; set; }
        public string Descripcion { get; set; }
        public decimal Monto { get; set; }
        public DateTime FechaLimite { get; set; }
        public bool EsVencido { get; set; }
        public bool EstaSeleccionado { get; set; }
        public bool EsPagado { get; set; }
        public string UniqueId { get; set; }
        public int IdRegistro { get; set; }
    }

    public class DetalleDeudaItem
    {
        public string Concepto { get; set; }
        public decimal Monto { get; set; }
    }

    public class ReciboViewModel
    {
        public string Evento { get; set; }
        public string Asistente { get; set; }
        public string Modalidad { get; set; }
        public string Concepto { get; set; }
        public string Descripcion { get; set; }
        public int NumeroPago { get; set; }
        public decimal Monto { get; set; }
        public DateTime FechaPago { get; set; }
        public string Folio { get; set; }
        public string UrlValidacion { get; set; }

        // Nuevo: Mostrar modalidad en recibo
        public string Subtipo { get; set; }

        // --- CAMPOS NUEVOS PARA CUPONES EN RECIBO INDIVIDUAL ---
        public decimal Descuento { get; set; }
        public string CodigoCupon { get; set; }
    }

    // =========================================================
    // 7. MODELO PARA EDICIÓN RÁPIDA
    // =========================================================
    public class AsistenteEdicionViewModel
    {
        public int Id_Asistente { get; set; }
        public int Id_Evento { get; set; }
        public string NombreCompleto { get; set; }
        public string EtiquetaGrupo { get; set; }
        public int Edad { get; set; }
        public string Genero { get; set; }
        public List<RespuestaItem> Respuestas { get; set; } = new List<RespuestaItem>();

        // Nuevo: Poder cambiar de modalidad
        public int? Id_Subtipo { get; set; }
    }

    public class AsistenteRegistrado
    {
        public int Id_Asistente { get; set; }
        public string NombreCompleto { get; set; }
        public bool EsPagado { get; set; }
        public int IdSolicitud { get; set; }
        public string EtiquetaGrupo { get; set; }
        public string TokenExterno { get; set; }
        public int PagosRealizados { get; set; }
        public int TotalPagosCalendario { get; set; }
        public decimal CostoReal { get; set; }
        public decimal CostoExtras { get; set; }
        public bool EstaLiquidadado => TotalPagosCalendario > 0 && PagosRealizados >= TotalPagosCalendario;
        public int Edad { get; set; }
        public string Genero { get; set; }
        public List<RespuestaItem> Respuestas { get; set; } = new List<RespuestaItem>();
        public bool TieneVencidos { get; set; }
        public bool EstaBloqueado { get; set; }
        public string EstatusTransaccion { get; set; }
        public int IdTransaccionPendiente { get; set; }

        // Nuevo: Info del Subtipo
        public string NombreSubtipo { get; set; }
        public int? IdSubtipo { get; set; }
        public decimal CostoBaseModalidad { get; set; }
        public List<DetalleProductoExtraAsistente> ProductosExtra { get; set; } = new List<DetalleProductoExtraAsistente>();
        public bool EstaAsignadoAlojamiento { get; set; }
        public int SegundosRestantesApartado { get; set; }
        public bool EncuestaRespondida { get; set; } = true;
    }

    public class DetalleProductoExtraAsistente
    {
        public int Id_ProductoExtra { get; set; }
        public string NombreProducto { get; set; }
        public decimal PrecioUnitario { get; set; }
        public int CantidadComprada { get; set; }
        public decimal Total { get; set; }
        public int CantidadPagada { get; set; }
        public int CantidadPendiente { get; set; }
        public int CantidadMinima { get; set; }
        public int CantidadMaxima { get; set; }
    }
    public class DetalleConceptoPago
    {
        public string Concepto { get; set; }
        public decimal Monto { get; set; }
        public string Persona { get; set; }
    }

    public class SubirComprobanteViewModel
    {
        public string Token { get; set; }
        public int IdEvento { get; set; }
        public string TituloEvento { get; set; }
        public decimal TotalPagar { get; set; }
        public List<int> IdsAsistentes { get; set; }
        public List<string> IdsPagosSeleccionados { get; set; }
        public IFormFile ArchivoComprobante { get; set; }
        public List<DetalleConceptoPago> Detalles { get; set; } = new List<DetalleConceptoPago>();

        public string ClaveCuentaBancaria { get; set; }

        // --- CAMPOS NUEVOS PARA CUPONES EN TRANSFERENCIA ---
        public decimal MontoDescuento { get; set; }
        public decimal SubTotalOriginal { get; set; } // Para mostrar el cálculo (Sub - Desc = Total)
        public string CodigoCupon { get; set; }
    }
    // Para que el ADMIN revise
    public class RevisionPagoViewModel
    {
        public int IdTransaccion { get; set; }
        public string UsuarioSolicitante { get; set; }
        public string DetallesPagos { get; set; }
        public decimal Monto { get; set; }
        public DateTime FechaIntento { get; set; }
        public int IdArchivoComprobante { get; set; }
        public string JsonDetalles { get; set; }
        public string ReferenciaEsperada { get; set; }
        public decimal MontoDescuento { get; set; }
        public string CodigoCupon { get; set; }
        public string TituloEvento { get; set; }
    }

    public class DashboardEventoViewModel
    {
        public List<EstadisticaPregunta> PreguntasFrecuentes { get; set; } = new List<EstadisticaPregunta>();
        public int IdEvento { get; set; }
        public string IdEventoEncriptado { get; set; }
        public string Titulo { get; set; }
        public DateTime Fecha { get; set; }

        // Estadísticas Globales
        public int TotalInscritos { get; set; }
        public int TotalPagados { get; set; }
        public decimal DineroRecaudado { get; set; }
        public decimal DineroPendiente { get; set; }
        public decimal ProyeccionTotal { get; set; }
        public int PorcentajeOcupacion { get; set; }

        // --- CAMPOS NUEVOS PARA DASHBOARD ---
        public decimal TotalDescuentosOtorgados { get; set; }

        // Listas para Gráficas
        public List<DatoGrafica> CronologiaRegistros { get; set; } = new List<DatoGrafica>();
        public List<DatoGrafica> Generos { get; set; } = new List<DatoGrafica>();

        // --- NUEVO: Estadísticas de Modalidades ---
        public List<DatoGrafica> SubtiposStats { get; set; } = new List<DatoGrafica>();

        // --- NUEVO: Lista de últimos inscritos ---
        public List<InscritoReciente> UltimosInscritos { get; set; } = new List<InscritoReciente>();

        // Análisis de preguntas (si ya lo tenías)
        public List<PreguntaAnalisis> AnalisisPreguntas { get; set; } = new List<PreguntaAnalisis>();
    }
    public class EstadisticaPregunta
    {
        public string Pregunta { get; set; }
        public List<DatoGrafica> Respuestas { get; set; } = new List<DatoGrafica>();
    }
    public class InscritoReciente
    {
        public string Nombre { get; set; }
        public DateTime FechaRegistro { get; set; }
        public string EstadoPago { get; set; } // "Pagado" o "Pendiente"
    }

    // Esta probablemente ya la tengas, pero por si acaso:
    public class DatoGrafica
    {
        public string Etiqueta { get; set; }
        public decimal Valor { get; set; }
    }

    // Esta también debería estar si usabas el dashboard anterior:
    public class PreguntaAnalisis
    {
        public string Pregunta { get; set; }
        public List<string> RespuestasFrecuentes { get; set; } = new List<string>();
    }

    public class PreguntaResumen
    {
        public string Pregunta { get; set; }
        public List<string> RespuestasFrecuentes { get; set; } = new List<string>();
    }

    public class AsistenteResumen
    {
        public string Nombre { get; set; }
        public string EstadoPago { get; set; }
        public DateTime FechaRegistro { get; set; }
        public decimal MontoPagado { get; set; }
    }

    public class ReciboGeneralViewModel
    {
        public string FolioGeneral { get; set; }
        public string Evento { get; set; }
        public string Asistente { get; set; }
        public string Modalidad { get; set; }
        public decimal TotalPagado { get; set; }
        public DateTime FechaUltimoPago { get; set; }
        public List<DetallePagoRecibo> Desglose { get; set; } = new List<DetallePagoRecibo>();
        public string UrlValidacion { get; set; }

        // --- CAMPOS NUEVOS PARA RECIBO GENERAL ---
        public decimal TotalDescuentos { get; set; }
    }

    public class DetallePagoRecibo
    {
        public string Concepto { get; set; }
        public string Descripcion { get; set; }
        public decimal Monto { get; set; }
        public DateTime Fecha { get; set; }
        public string Referencia { get; set; }
    }
    public class GestionInscritosViewModel
    {
        public int IdEvento { get; set; }
        public string IdEventoEncriptado { get; set; }
        public string TituloEvento { get; set; }
        public bool EsAbandonado { get; set; }
        public List<GrupoModalidad> Grupos { get; set; } = new List<GrupoModalidad>();
    }

    public class GrupoModalidad
    {
        public int IdSubtipo { get; set; }
        public string NombreModalidad { get; set; }
        public List<AsistenteGestion> Asistentes { get; set; } = new List<AsistenteGestion>();
    }

    public class AsistenteGestion
    {
        public int IdAsistente { get; set; }
        public string TokenExterno { get; set; }
        public int Edad { get; set; }
        public string NombreCompleto { get; set; }
        public string Genero { get; set; }
        public decimal Score { get; set; }
        public bool EsPagadoGlobal { get; set; }
        public bool EsAbandonado { get; set; }

        public decimal CostoOficialPaquete { get; set; }
        public decimal TotalCuentasAsignadas { get; set; }
        public bool TieneAjusteFinanciero { get; set; }

        // Caso 1: El costo no coincide y no hubo cobro extra (Se regaló el cambio)
        public bool EsTraspasoRespetado => (TotalCuentasAsignadas != CostoOficialPaquete && CostoOficialPaquete > 0 && !TieneAjusteFinanciero);

        // Caso 2: Sí hubo cobro extra / devolución (Tiene el concepto Numero_Pago = -1)
        public bool EsTraspasoConAjuste => TieneAjusteFinanciero;

        public bool EncuestaRespondida { get; set; } = true;

        public List<PagoGestion> Pagos { get; set; } = new List<PagoGestion>();
    }

    public class PagoGestion
    {
        public int IdCuenta { get; set; }
        public string Concepto { get; set; }
        public decimal Monto { get; set; }
        public bool Pagado { get; set; }
        public DateTime? FechaPago { get; set; }
        public string Referencia { get; set; }
    }
    public class DistribucionAlojamientosViewModel
    {
        public int IdEvento { get; set; }
        public string IdEventoEncriptado { get; set; }
        public string TituloEvento { get; set; }
        public List<SubtipoEventoItem> Subtipos { get; set; } = new List<SubtipoEventoItem>();
        public List<AsistenteAlojamiento> NoAsignados { get; set; } = new List<AsistenteAlojamiento>();
        public List<ZonaAlojamiento> Zonas { get; set; } = new List<ZonaAlojamiento>();
        public int CupoMaximoEvento { get; set; }
        public int TotalLugaresCreados { get; set; }
        public Dictionary<int, int> LugaresCreadosPorSubtipo { get; set; } = new Dictionary<int, int>();
        public ConfigPortalItem ConfigPortal { get; set; }
        public List<AgrupacionModalidadItem> Agrupaciones { get; set; } = new List<AgrupacionModalidadItem>();
    }

    public class AsistenteAlojamiento
    {
        public int IdAsistente { get; set; }
        public string Nombre { get; set; }
        public int Edad { get; set; }
        public string Genero { get; set; } // 'H' o 'M'
        public int? IdSubtipo { get; set; }
        public string NombreSubtipo { get; set; }
        public decimal ScoreJusticia { get; set; }
        public bool EsCortesia { get; set; }
    }

    public class AlojamientoMemoria
    {
        public int IdAlojamiento { get; set; }
        public string GeneroAsignado { get; set; }
        public int IdSubtipo { get; set; }
        public int EspacioDisponible { get; set; }
    }
    public class AlojamientoItem
    {
        public int IdAlojamiento { get; set; }
        public string Nombre { get; set; }
        public string GeneroAsignado { get; set; } // 'H', 'M' o null
        public List<CapacidadAlojamiento> Capacidades { get; set; } = new List<CapacidadAlojamiento>();
        public bool PublicadoPortal { get; set; }
    }

    public class CapacidadAlojamiento
    {
        public int IdCapacidad { get; set; }
        public int IdSubtipo { get; set; }
        public string NombreSubtipo { get; set; }
        public int CapacidadTotal { get; set; }
        public List<AsistenteAlojamiento> Ocupantes { get; set; } = new List<AsistenteAlojamiento>();
        public int? IdAgrupacion { get; set; }
        public string NombreAgrupacion { get; set; }
    }
    public class CrearAlojamientoRequest
    {
        public int IdZona { get; set; }
        public string Nombre { get; set; }
        public string GeneroAsignado { get; set; } // Puede ser 'H', 'M', 'X' (Mixto) o "" (Neutral)
        public Dictionary<int, int> Capacidades { get; set; }
    }

    public class EditarAlojamientoRequest
    {
        public int IdAlojamiento { get; set; }
        public string Nombre { get; set; }
        public string GeneroAsignado { get; set; }
        public Dictionary<int, int> Capacidades { get; set; }
    }
    public class CrearZonaRequest
    {
        public int IdEvento { get; set; }
        public int IdZona { get; set; } // 0 si es nueva
        public string Nombre { get; set; }
        public List<int> SubtiposPermitidos { get; set; }
    }
    public class ZonaAlojamiento
    {
        public int IdZona { get; set; }
        public string Nombre { get; set; }

        // Lista con los IDs de las modalidades permitidas en esta zona
        public List<int> SubtiposPermitidos { get; set; } = new List<int>();

        // Lista de habitaciones o cabañas dentro de esta zona
        public List<AlojamientoItem> Alojamientos { get; set; } = new List<AlojamientoItem>();
    }

    public class AsistenteRaw
    {
        public int IdAsistente { get; set; }
        public string Genero { get; set; }
        public int IdSubtipo { get; set; }
        public int IdRegistrador { get; set; }
        public int PagosHechos { get; set; }
        public int TotalPagos { get; set; }

        public bool TieneCuarto { get; set; }
    }

    public class AsistentePendiente
    {
        public int IdAsistente { get; set; }
        public string Genero { get; set; }
        public int IdSubtipo { get; set; }
        public int IdRegistrador { get; set; }
        public decimal ScoreJusticia { get; set; }
    }

    public class BloqueAsignacion
    {
        public int IdRegistrador { get; set; }
        public string Genero { get; set; }
        public int IdSubtipo { get; set; }
        public decimal ScoreJusticia { get; set; }
        public List<AsistentePendiente> Asistentes { get; set; }
    }

    public class CuartoInfo
    {
        public int IdAlojamiento { get; set; }
        public string GeneroAsignado { get; set; }
        public Dictionary<int, int> Capacidades { get; set; }
        public Dictionary<int, int> Ocupados { get; set; }
        public HashSet<int> RegistradoresEnCuarto { get; set; }
    }

    public class CuartoInfoHibrido
    {
        public int IdAlojamiento { get; set; }
        public string GeneroAsignado { get; set; }
        public Dictionary<string, int> Capacidades { get; set; }
        public Dictionary<string, int> Ocupados { get; set; }
        public HashSet<int> RegistradoresEnCuarto { get; set; }
    }
    public class AsignacionMultipleRequest
    {
        public int IdAlojamiento { get; set; }
        public List<int> IdsAsistentes { get; set; }
        public string RefDestino { get; set; }
    }

    public class DesasignacionMultipleRequest
    {
        public List<int> IdsAsistentes { get; set; }
    }

    public class AutoAcomodoRequest
    {
        public int IdEvento { get; set; }
        public List<int> IdsSeleccionados { get; set; }
    }
    public class EditorGraficoViewModel
    {
        public int IdEvento { get; set; }
        public string IdEventoEncriptado { get; set; }
        public string TituloEvento { get; set; }
        public string ImagenUrlActual { get; set; }
        public List<ItemModalidadGrafico> Modalidades { get; set; } = new List<ItemModalidadGrafico>();
    }

    public class ItemModalidadGrafico
    {
        public int IdSubtipo { get; set; }
        public string Nombre { get; set; }
        public decimal X { get; set; }
        public decimal Y { get; set; }
        public bool Configurado { get; set; }
    }

    public class ReservaRapidaRequest
    {
        public int IdEvento { get; set; }
        public List<ReservaItem> Seleccion { get; set; }
    }
    public class ReservaItem
    {
        public int IdEvento { get; set; }
        public int IdSubtipo { get; set; }
        public int Cantidad { get; set; }
    }
    public class CapacidadHibridaRequest
    {
        public int? IdSubtipo { get; set; }
        public int? IdAgrupacion { get; set; }
        public int CapacidadTotal { get; set; }
    }

    public class EditarAlojamientoHibridoRequest
    {
        public int IdAlojamiento { get; set; }
        public int IdZona { get; set; }
        public string Nombre { get; set; }
        public string GeneroAsignado { get; set; }
        public List<CapacidadHibridaRequest> CapacidadesArray { get; set; } = new List<CapacidadHibridaRequest>();
    }

    public class ConfigPortalRequest
    {
        public int IdEvento { get; set; }
        public bool PortalActivo { get; set; }
        public bool SoloPagados { get; set; }
        public bool MostrarTodasLasHabitaciones { get; set; }
        public string MensajeCerrado { get; set; }
    }
    public class ConfigPortalItem
    {
        public bool Portal_Activo { get; set; }
        public bool Solo_Pagados { get; set; }
        public bool MostrarTodasLasHabitaciones { get; set; }
        public string Mensaje_Portal_Cerrado { get; set; }
    }

    public class AgrupacionModalidadItem
    {
        public int Id_Agrupacion { get; set; }
        public string Clave_Tecnica { get; set; }
        public string Nombre_Descriptivo { get; set; }
        public List<string> NombresSubtipos { get; set; } = new List<string>();
    }
    public class CrearAgrupacionRequest
    {
        public int IdEvento { get; set; }
        public string Nombre { get; set; }
        public List<int> Subtipos { get; set; }
    }
    public class PortalAlojamientoViewModel
    {
        public int IdEvento { get; set; }
        public string IdEventoEncriptado { get; set; }
        public string TituloEvento { get; set; }
        public bool PortalActivo { get; set; }
        public bool MostrarTodasLasHabitaciones { get; set; }
        public string MensajeInactivo { get; set; }
        public bool BloqueoPorDeuda { get; set; }

        public List<MiembroGrupoAlojamiento> MiGrupo { get; set; } = new List<MiembroGrupoAlojamiento>();
        public List<HabitacionDisponiblePortal> Habitaciones { get; set; } = new List<HabitacionDisponiblePortal>();
    }

    public class MiembroGrupoAlojamiento
    {
        public int IdAsistente { get; set; }
        public string Nombre { get; set; }
        public string Genero { get; set; }
        public int? IdSubtipo { get; set; }
        public string TipoAlojamiento { get; set; } // Nombre del Subtipo o Agrupación
        public string RefTipo { get; set; } // Formato: "SUB_X" o "AGR_Y"
        public bool TieneDeuda { get; set; }

        // Si ya está asignado
        public int? IdAlojamientoActual { get; set; }
        public string NombreHabitacionActual { get; set; }
        public string NombreZonaActual { get; set; }
    }

    public class HabitacionDisponiblePortal
    {
        public int IdAlojamiento { get; set; }
        public string NombreHabitacion { get; set; }
        public string NombreZona { get; set; }
        public string GeneroAsignado { get; set; } // H, M, X o null

        public List<CapacidadPortal> Capacidades { get; set; } = new List<CapacidadPortal>();
    }

    public class CapacidadPortal
    {
        public string RefTipo { get; set; } // "SUB_X" o "AGR_Y"
        public string NombreTipo { get; set; }
        public int CapacidadTotal { get; set; }
        public int LugaresOcupados { get; set; }
        public List<string> GenerosOcupantes { get; set; } = new List<string>(); // Solo géneros por privacidad
    }

    public class AsignacionPortalRequest
    {
        public int IdAlojamiento { get; set; }
        public List<int> IdsAsistentes { get; set; }
        public string RefTipo { get; set; } // Qué capacidad van a consumir
    }

    public class StatGeneroInfo
    {
        public int espacios { get; set; } = 0;
        public int ocupados { get; set; } = 0;
        public int porAsignar { get; set; } = 0;
        public int enProceso { get; set; } = 0;
        public List<object> lista { get; set; } = new List<object>();
        public int libres => Math.Max(0, espacios - ocupados);
    }
    // =========================================================
    // 8. RITMO DE INSCRIPCIONES Y AUDITORÍA
    // =========================================================
    public class RitmoInscripcionesViewModel
    {
        public string IdEventoEncriptado { get; set; }
        public int IdEvento { get; set; }
        public string TituloEvento { get; set; }

        // Filtros
        public DateTime FechaCorte { get; set; }
        public decimal PorcentajeMeta { get; set; }
        public int? IdSubtipoFiltro { get; set; }

        public List<SubtipoEventoItem> SubtiposDisponibles { get; set; } = new List<SubtipoEventoItem>();
        public List<ItemAvancePago> Resultados { get; set; } = new List<ItemAvancePago>();

        // KPIs Dinámicos
        public int TotalEvaluados => Resultados.Count;
        public int TotalCumplieron => Resultados.Count(x => x.PorcentajeAlcanzado >= PorcentajeMeta);
        public decimal TotalRecaudadoAlCorte => Resultados.Sum(x => x.MontoPagadoAlCorte);
    }

    public class ItemAvancePago
    {
        public int IdAsistente { get; set; }
        public string Asistente { get; set; }
        public string Inscriptor { get; set; }
        public string Modalidad { get; set; }
        public decimal CostoOficial { get; set; }
        public decimal MontoPagadoAlCorte { get; set; }
        public decimal PorcentajeAlcanzado => CostoOficial > 0 ? Math.Round((MontoPagadoAlCorte / CostoOficial) * 100, 2) : 100;
    }

    public class DetallePagoModalItem
    {
        public string Concepto { get; set; }
        public decimal MontoPagado { get; set; }
        public string FechaPago { get; set; }
        public string Metodo { get; set; }
        public string Referencia { get; set; }
    }
}