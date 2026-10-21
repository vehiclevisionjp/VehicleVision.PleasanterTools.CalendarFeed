# CalDAV の実クライアント（Python の caldav ライブラリ・vdirsyncer）を入れた検証用のイメージ。
# **ホストに python も curl も要求しない**。使い捨て（本アプリの配布物ではない）。
FROM python:3.13-slim

# 版は pip の解決に任せず、検証した版を run.sh が表示する（pip freeze）。古い版で通ったことを、新しい版の保証にしない
RUN pip install --no-cache-dir caldav vdirsyncer

COPY clients/ /check/
WORKDIR /check
