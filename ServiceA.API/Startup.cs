using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Polly;
using Polly.Extensions.Http;

namespace ServiceA.API
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Service A API", Version = "v1" });
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = @"JWT Authorization header using the Bearer scheme. \r\n\r\n 
                      Enter 'Bearer' [space] and then your token in the text input below.
                      \r\n\r\nExample: 'Bearer 12345abcdef'",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "Bearer"
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement()
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                },
                            Scheme = "oauth2",
                            Name = "Bearer",
                            In = ParameterLocation.Header,
                        },
                        new List<string>()
                    }
                });

            });
            services.AddHttpClient<ProductService>(opt =>
            {
                opt.BaseAddress = new Uri("https://localhost:5003/api/products/");
            }).AddPolicyHandler(GetAdvanceCircuitBreakerPolicy());
        }

        private IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
        {
            return HttpPolicyExtensions.HandleTransientHttpError().CircuitBreakerAsync(3, TimeSpan.FromSeconds(10),
            onBreak: (arg1, arg2) =>
            {
                Debug.WriteLine("Circuit Breaker => On Break");
            },
            onReset: () =>
            {
                Debug.WriteLine("Circuit Breaker => On Reset");
            },
            onHalfOpen: () =>
            {
                Debug.WriteLine("Circuit Breaker => On Half Open");
            });
        }

        private IAsyncPolicy<HttpResponseMessage> GetAdvanceCircuitBreakerPolicy()
        {
            return HttpPolicyExtensions.HandleTransientHttpError().AdvancedCircuitBreakerAsync (0.5, TimeSpan.FromSeconds(30), 10, TimeSpan.FromSeconds(50),
            onBreak: (arg1, arg2) =>
            {
                Debug.WriteLine("Circuit Breaker => On Break");
            },
            onReset: () =>
            {
                Debug.WriteLine("Circuit Breaker => On Reset");
            },
            onHalfOpen: () =>
            {
                Debug.WriteLine("Circuit Breaker => On Half Open");
            }); 
        }

        private IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
        {
            //StatusCode NotFound ise 5 kez bu isteği tekrarla, tekrarlar arasında 10 saniye bekle:
            return HttpPolicyExtensions.HandleTransientHttpError().OrResult(msg => msg.StatusCode == HttpStatusCode.NotFound)
            .WaitAndRetryAsync(5, retryAtempt =>
            {
                Debug.WriteLine($"Retry Count: {retryAtempt}");
                return TimeSpan.FromSeconds(10);
            }, onRetryAsync: OnRetryAsync);
        }

        private Task OnRetryAsync(DelegateResult<HttpResponseMessage> arg1, TimeSpan arg2)
        {
            Debug.WriteLine($"Request made again: {arg2.TotalMilliseconds }");
            return Task.CompletedTask;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "WebAPI v1"));
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}


/*Basic level Circuit Breaker, devre Closed durumunda iken timer tutmaz. Ard arda gönderilen 3 request başarısız sonuçlanırsa 
devrenin durumu Open'a alınır. Devre Open durumunda 10 saniye bekler ve ardından Half-Open durumuna geçer. Sonrasında gelen
request eğer başarısız olursa Open'a döner bir 10 saniye daha bekler, başarılı olursa Closed durumuna geçer.

!!!onBreak, onReset, onHalfOpen ile devrenin durumları arasındaki geçişte business kodu çalıştırılabilir.

-BASIC CIRCUIT PATTERN İLE YAPILAN RUN'DA:
Down olan Service B'ye 10 saniye içinde 3 request yapıldığında yapılan her request için HttpRequestException döner. 3. requestten sonra 
Service B'ye bile ulaşmadan BrokenCircuitException fırlatılarak 10 saniye bekler, bu 10 saniye içinde yapılan tüm istekler BrokenCircuitException
fırlatır( o 10 saniyelik sürede B servisi ayağa kalsa bile bu exception döner). 10 saniye sonunda yapılan istek B servisinden
HttpRequestException dönerse devre Open'a geçip bir 10 saniye daha bekler. Eğer başarılı dönerse devre Closed durumuna geçer.

-ADVANCED CIRCUIT PATTERN İLE YAPILAN RUN'DA:
(0.5, TimeSpan.FromSeconds(30), 5, TimeSpan.FromSeconds(50), onBreak:....)
Yukarıda geçen parametrelerden TimeSpan.FromSeconds(30) devre Closed durumunda iken sürekli çalışan timer'ı ifade eder. 30 saniyelik süreçte yapılan
isteklerin 0.5'i yani %50'si başarısız olursa devre Open durumuna geçer. Bu isteklerin yarısı belirli bir threshold'u geçemezse (şuanki durumda
5 istek şeklinde set edilen parametre) timer tekrar başa sarar ve devre Closed'da kalmaya devam eder. Örneğin 30 saniye içinde 20 istek yapıldı
ve bu isteklerden 11 tanesi başarısız olursa (%50'yi ve 5 thresholdunu geçti) devre Open'a geçer. Devre Open evresinde iken 50 saniye sayar, bu
süre içinde gelen tüm istekler B servisine iletilmeden direkt BrokenCircuitException fırlatır. 50 saniyeden sonra Half-Open evresine girer. Bu
evrede gelen istek B servisine gider oradan başarılı sonuç dönerse devre Closed evresine alınır ve 30 saniyelik ilk timer çalışmaya başlar.
Başarısız olması durumunda tekrar Open evresine girer ve tekrar 50 saniye saymaya başlar.

 */