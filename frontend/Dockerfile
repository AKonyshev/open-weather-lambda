FROM nginx:1.23.1

COPY ./nginx.conf /etc/nginx/nginx.conf
WORKDIR /usr/share/nginx

RUN rm -rfv ./html

COPY ./static/ ./html/
ENTRYPOINT ["nginx", "-g", "daemon off;"]